using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoPushStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _remoteRoot;
    private const string ProjectName = "demo";

    public AutoPushStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-auto-push-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        _remoteRoot = Path.Combine(_tempDir, "origin.git");
        Directory.CreateDirectory(_watchPath);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_tempDir, "init", "--bare", "-q", "--initial-branch=main", _remoteRoot);
        RunGit(_repoRoot, "init", "-q", "-b", "main");
        RunGit(_repoRoot, "config", "user.email", "test@example.com");
        RunGit(_repoRoot, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed");
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", "seed");
        RunGit(_repoRoot, "remote", "add", "origin", _remoteRoot);
        RunGit(_repoRoot, "push", "-q", "-u", "origin", "main");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // PERF regression guard: the move-to-6-completed request used to await the
    // git fetch + git push inline, so a "move to complete" blocked for 2-3 s on
    // the network round-trip. With a CompletedPushQueue wired, MoveAsync only
    // enqueues a snapshot and returns; the push runs on CompletedPushWorker.
    // A 3 s pre-push hook simulates a slow remote: on the broken (synchronous)
    // code this test measured ~3700 ms; with the queue it returns in tens of ms.
    // MachineBound 19.07.: Wallclock-Latenzbudget (<1000ms) flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task MoveToCompleted_OffloadsSlowPushFromRequestPath()
    {
        InstallSlowPushHook(3);
        var sha = CommitLocalChange("slow push change");
        WriteJob(TaskStates.HumanReview, "slow-task", sha);
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await deps.Transitions.MoveAsync(
            "slow-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);
        sw.Stop();

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"move-to-completed took {sw.ElapsedMilliseconds}ms; the git push must be off the request path");
        // The push was queued, not performed inline.
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal("slow-task", queued!.Job.Id);
    }

    // Companion to the latency guard: prove the queued push actually lands when
    // the CompletedPushWorker drains the queue, so offloading did not silently
    // drop the auto-push.
    [Fact]
    public async Task CompletedPushWorker_PushesQueuedCommitToMain()
    {
        var sha = CommitLocalChange("worker-pushed change");
        WriteJob(TaskStates.HumanReview, "worker-task", sha);
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var outcome = await deps.Transitions.MoveAsync(
            "worker-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotEqual(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        var pushed = await WaitUntilAsync(
            () => RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main") == sha,
            TimeSpan.FromSeconds(15));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(pushed, "worker did not push the queued completed commit within the timeout");
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task AlwaysImmediate_QueuesAutoCommitPushOffTransitionPath()
    {
        InstallSlowPushHook(3);
        WriteJobWithoutCommit(TaskStates.Progress, "immediate-task");
        var sessionLogs = Path.Combine(_watchPath, TaskStates.Progress, "immediate-task", "logs");
        Directory.CreateDirectory(sessionLogs);
        File.WriteAllText(Path.Combine(sessionLogs, "session-events.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddSeconds(-1), Kind = "start", Cli = "codex"
            }) + Environment.NewLine);
        File.WriteAllText(Path.Combine(_repoRoot, "immediate.txt"), "push me\n");
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");

        var outcome = await deps.Transitions.MoveAsync(
            "immediate-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.False(queued!.RequireCompletedState);
        var localHead = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
        Assert.NotEqual(remoteBefore, localHead);
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.ProcessAsync(queued, CancellationToken.None);

        Assert.Equal(localHead, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    // AGT-2688: with a develop line present the raw platform commit must never
    // be aimed at main (the lineage guard refuses it by construction, and the
    // old code then replayed that refusal forever). It belongs on the work line,
    // where it is a plain fast-forward, and main stays untouched.
    [Fact]
    public async Task AlwaysImmediate_WithDevelopLine_PublishesToDevelopAndLeavesMainUntouched()
    {
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");
        SeedDevelopLine();
        WriteJobWithoutCommit(TaskStates.Progress, "lineage-task");
        var sessionLogs = Path.Combine(_watchPath, TaskStates.Progress, "lineage-task", "logs");
        Directory.CreateDirectory(sessionLogs);
        File.WriteAllText(Path.Combine(sessionLogs, "session-events.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddSeconds(-1), Kind = "start", Cli = "codex"
            }) + Environment.NewLine);
        File.WriteAllText(Path.Combine(_repoRoot, "lineage.txt"), "must integrate through develop\n");
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var outcome = await deps.Transitions.MoveAsync(
            "lineage-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(queue.Reader.TryRead(out var queued));
        var rawCommit = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
        Assert.NotEqual(remoteBefore, rawCommit);

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.ProcessAsync(queued!, CancellationToken.None);

        Assert.Equal(rawCommit, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop"));
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    // The scenario the fleet actually ran into: origin/develop moved ahead while
    // the card was in review, so the backend's local develop is behind. The push
    // must still land as a fast-forward once the local line is caught up, and it
    // must land on develop rather than being refused against main.
    [Fact]
    public async Task Backstop_DivergedDevelop_PublishesFastForwardOntoDevelop()
    {
        SeedDevelopLine();
        var remoteAdvance = AdvanceOriginDevelopFromSecondClone("operator change while the card was in review");
        // Local develop is now behind origin/develop; catching it up is what the
        // integration path does before every merge.
        RunGit(_repoRoot, "fetch", "-q", "origin", "develop");
        RunGit(_repoRoot, "merge", "-q", "--ff-only", "origin/develop");
        var sha = CommitLocalChange("delivery on the caught-up develop line");
        WriteJob(TaskStates.Completed, "diverged-develop-task", sha);
        var deps = BuildDeps();
        var backstop = BuildBackstop(deps);

        var pushed = await backstop.RunOnceAsync();

        Assert.Equal(1, pushed);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop"));
        // Fast-forward, not a force: the operator's commit is still an ancestor.
        Assert.Equal(0, RunGitRaw(_repoRoot, "merge-base", "--is-ancestor", remoteAdvance, sha).Code);
    }

    // A genuinely non-fast-forward push is a decision about immutable inputs, so
    // replaying it can only ever produce the same refusal. The backstop must
    // report it once and then stop attempting it, instead of re-emitting the
    // same line on every sweep forever (AGT-2688: 570+ identical warnings).
    [Fact]
    public async Task Backstop_NonFastForwardPush_IsBlockedOnceAndNotRetried()
    {
        SeedDevelopLine();
        var sha = CommitLocalChange("local delivery that will lose the race");
        AdvanceOriginDevelopFromSecondClone("conflicting operator change");
        WriteJob(TaskStates.Completed, "non-ff-task", sha);
        var deps = BuildDeps();
        var backstop = BuildBackstop(deps);
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop");

        var first = await backstop.RunOnceAsync();

        Assert.Equal(0, first);
        // The guard never force-pushes: origin keeps the operator's commit.
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop"));

        // Second sweep: the refusal is remembered, so no further push is issued.
        var pushProbe = InstallPushCounter();
        var second = await backstop.RunOnceAsync();

        Assert.Equal(0, second);
        Assert.False(File.Exists(pushProbe), "the backstop re-attempted a push that is already blocked for good");
    }

    /// <summary>
    /// Reproduces the fleet's real topology: a work line and a release line,
    /// with <c>origin/HEAD</c> pointing at the release line the way a clone
    /// leaves it. That last detail is what made production resolve the auto-push
    /// target to <c>main</c> and hit the lineage guard on every completed card.
    /// </summary>
    // The exact shape of the managed repository that produced the overnight log
    // flood: HEAD on main, a local develop branch that was never published, and
    // no origin/develop at all. The lineage guard reads "this repo has a develop
    // line" from the local branch alone, so every raw completed commit aimed at
    // main was refused - and the target was hard-coded to main, so every card
    // was aimed there. The commit must instead publish the work line, which also
    // creates origin/develop the first time.
    [Fact]
    public async Task Backstop_LocalDevelopNeverPublished_PublishesWorkLineInsteadOfBeingLineageBlocked()
    {
        RunGit(_repoRoot, "branch", "develop");
        var mainBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");
        Assert.NotEqual(0, RunGitRaw(_repoRoot, "rev-parse", "--verify", "refs/remotes/origin/develop").Code);

        var sha = CommitLocalChange("completed work that used to be lineage-blocked");
        WriteJob(TaskStates.Completed, "unpublished-develop-task", sha);
        var deps = BuildDeps();
        deps.Settings.SetIntegrationBranch(ProjectName, "develop");
        var backstop = BuildBackstop(deps);

        var pushed = await backstop.RunOnceAsync();

        Assert.Equal(1, pushed);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop"));
        Assert.Equal(mainBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    private void SeedDevelopLine()
    {
        RunGit(_repoRoot, "checkout", "-q", "-b", "develop");
        RunGit(_repoRoot, "push", "-q", "-u", "origin", "develop");
        RunGit(_repoRoot, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
    }

    /// <summary>
    /// Records that a push happened at all. The blocked-path assertion is about
    /// the absence of a retry, which a return value of 0 alone cannot prove.
    /// </summary>
    private string InstallPushCounter()
    {
        var marker = Path.Combine(_tempDir, "push-attempted.marker");
        var hooksDir = Path.Combine(_repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hook = Path.Combine(hooksDir, "pre-push");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch \"{marker}\"\nexit 0\n");
        File.SetUnixFileMode(
            hook,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return marker;
    }

    private string AdvanceOriginDevelopFromSecondClone(string content)
    {
        var clone = Path.Combine(_tempDir, "advance-clone-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(_tempDir, "clone", "-q", "-b", "develop", _remoteRoot, clone);
        RunGit(clone, "config", "user.email", "remote@example.com");
        RunGit(clone, "config", "user.name", "remote");
        File.WriteAllText(Path.Combine(clone, "operator.txt"), content);
        RunGit(clone, "add", "-A");
        RunGit(clone, "commit", "-q", "-m", $"chore: {content}");
        RunGit(clone, "push", "-q", "origin", "develop");
        return RunGitCapture(clone, "rev-parse", "HEAD");
    }

    private CompletedPushBackstopHostedService BuildBackstop(Deps deps)
        => new(
            deps.Scanner,
            deps.Settings,
            deps.Transitions,
            deps.Config,
            NullLogger<CompletedPushBackstopHostedService>.Instance);

    private void InstallSlowPushHook(int seconds)
    {
        var hooksDir = Path.Combine(_repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hook = Path.Combine(hooksDir, "pre-push");
        File.WriteAllText(hook, $"#!/bin/sh\nsleep {seconds}\nexit 0\n");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task MoveToCompleted_PushesStampedCommitToMain()
    {
        var sha = CommitLocalChange("reviewed change");
        WriteJob(TaskStates.HumanReview, "reviewed-task", sha);
        var deps = BuildDeps();

        var outcome = await deps.Transitions.MoveAsync(
            "reviewed-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task MoveToCompleted_WhenStrategyNever_DoesNotPush()
    {
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");
        var sha = CommitLocalChange("manual push later");
        WriteJob(TaskStates.HumanReview, "manual-task", sha);
        var deps = BuildDeps();
        deps.Settings.SetAutoPushStrategy(ProjectName, AutoPushStrategies.Never);

        var outcome = await deps.Transitions.MoveAsync(
            "manual-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Backstop_PushesCompletedCommitMissedByTransition()
    {
        var sha = CommitLocalChange("missed trigger");
        WriteJob(TaskStates.Completed, "completed-task", sha);
        var deps = BuildDeps();
        var backstop = new CompletedPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Transitions,
            deps.Config,
            NullLogger<CompletedPushBackstopHostedService>.Instance);

        var pushed = await backstop.RunOnceAsync();

        Assert.Equal(1, pushed);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task MoveToCompleted_DoesNotForcePushDivergedRemote()
    {
        var localSha = CommitLocalChange("local reviewed change");
        var remoteSha = CommitFromSecondClone("remote operator change");
        WriteJob(TaskStates.HumanReview, "diverged-task", localSha);
        var deps = BuildDeps();

        var outcome = await deps.Transitions.MoveAsync(
            "diverged-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(remoteSha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    private Deps BuildDeps(CompletedPushQueue? pushQueue = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance, sessions: sessions, pushQueue: pushQueue);
        return new Deps(config, scanner, settings, transitions);
    }

    private string CommitLocalChange(string content)
    {
        File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), content);
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", $"feat: {content}");
        return RunGitCapture(_repoRoot, "rev-parse", "HEAD");
    }

    private string CommitFromSecondClone(string content)
    {
        var clone = Path.Combine(_tempDir, "second-clone");
        RunGit(_tempDir, "clone", "-q", _remoteRoot, clone);
        RunGit(clone, "config", "user.email", "remote@example.com");
        RunGit(clone, "config", "user.name", "remote");
        File.WriteAllText(Path.Combine(clone, "remote.txt"), content);
        RunGit(clone, "add", "-A");
        RunGit(clone, "commit", "-q", "-m", $"feat: {content}");
        RunGit(clone, "push", "-q", "origin", "main");
        return RunGitCapture(clone, "rev-parse", "HEAD");
    }

    private void WriteJob(string state, string slug, string sha)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $$"""
            {
              "id": "{{slug}}",
              "title": "{{slug}}",
              "state": "{{state}}",
              "order": 1,
              "agent": "copilot",
              "commit": {
                "sha": "{{sha}}",
                "shortSha": "{{sha[..7]}}",
                "message": "feat: {{slug}}",
                "filesChanged": 1,
                "files": ["work.txt"],
                "at": "{{DateTime.UtcNow:o}}"
              }
            }
            """);
    }

    private void WriteJobWithoutCommit(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $$"""
            {
              "id": "{{slug}}",
              "title": "{{slug}}",
              "state": "{{state}}",
              "order": 1,
              "agent": "copilot"
            }
            """);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = RunGitRaw(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = RunGitRaw(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr}");
        return stdout.Trim();
    }

    private static (string Stdout, string Stderr, int Code) RunGitRaw(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The fixture's "origin" is a bare repo; on hosts where git has
        // safe.bareRepository=explicit, plain `git -C <bare> ...` is refused.
        // Relax it per-invocation (subprocess-scoped, no config mutation).
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (stdout, stderr, p.ExitCode);
    }

    private sealed record Deps(
        IConfiguration Config,
        TaskScannerService Scanner,
        ProjectSettingsService Settings,
        TaskTransitionService Transitions);
}
