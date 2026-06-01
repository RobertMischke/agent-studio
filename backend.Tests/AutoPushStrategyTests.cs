using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

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
    [Fact]
    public async Task MoveToCompleted_OffloadsSlowPushFromRequestPath()
    {
        InstallSlowPushHook(3);
        var sha = CommitLocalChange("slow push change");
        WriteJob(TaskStates.HumanReview, "slow-task", sha);
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await deps.Transitions.MoveAsync("slow-task", TaskStates.Completed, _watchPath);
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

        var outcome = await deps.Transitions.MoveAsync("worker-task", TaskStates.Completed, _watchPath);
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

        var outcome = await deps.Transitions.MoveAsync("reviewed-task", TaskStates.Completed, _watchPath);

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

        var outcome = await deps.Transitions.MoveAsync("manual-task", TaskStates.Completed, _watchPath);

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

        var outcome = await deps.Transitions.MoveAsync("diverged-task", TaskStates.Completed, _watchPath);

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
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance, sessions: null, pushQueue: pushQueue);
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
        File.WriteAllText(Path.Combine(dir, "job.json"),
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
