using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceArtifactCommitServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceArtifactCommitService _service;

    public WorkspaceArtifactCommitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-workspace-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit(_root, "init", "-q", "-b", "main");
        RunGit(_root, "config", "user.name", "test");
        RunGit(_root, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_root, "README.md"), "seed\n");
        RunGit(_root, "add", "README.md");
        RunGit(_root, "commit", "-q", "-m", "seed");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _service = new WorkspaceArtifactCommitService(
            config,
            NullLogger<WorkspaceArtifactCommitService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void RunBoundaryCommit_StagesOnlyTheJobFolder()
    {
        var job = JobFolder("ASS-1");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "code-review.md"), "review\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        File.WriteAllText(Path.Combine(job, "logs", "session-events.jsonl"), "{}\n");

        var foreign = JobFolder("ASS-2");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "status.md"), "foreign\n");
        RunGit(_root, "add", Relative(Path.Combine(foreign, "status.md")));

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-1",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.Reissue);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(1, result.RunIndex);
        Assert.Equal("aspect-code-quality=warn", result.Steps);

        var committed = RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Contains(Relative(Path.Combine(job, "code-review.md")), committed);
        Assert.Contains(Relative(Path.Combine(job, "pipeline-execution.json")), committed);
        Assert.DoesNotContain(Relative(Path.Combine(foreign, "status.md")), committed);

        var status = RunGitCapture(_root, "status", "--porcelain=v1");
        Assert.Contains("ASS-2/status.md", status.Replace('\\', '/'));

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Run-Index: 1", message);
        Assert.Contains("Verdict: reissue", message);
        Assert.Contains("Steps: aspect-code-quality=warn", message);
    }

    [Fact]
    public void RunBoundaryCommit_TwoRunsProduceTwoFollowHistoryEntries()
    {
        var job = JobFolder("ASS-7");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        var codeReview = Path.Combine(job, "code-review.md");
        var sessions = Path.Combine(job, "logs", "session-events.jsonl");

        File.WriteAllText(codeReview, "run 1 review\n");
        File.WriteAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        var first = _service.TryCommitRunBoundary(_root, "ASS-7", null, job, ReviewDecisionKind.Reissue);
        Assert.True(first.Success, first.Error);
        Assert.True(first.DidCommit);

        File.WriteAllText(codeReview, "run 2 review\n");
        File.AppendAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Pass"));
        var second = _service.TryCommitRunBoundary(_root, "ASS-7", job, job, ReviewDecisionKind.AcceptAsDone);
        Assert.True(second.Success, second.Error);
        Assert.True(second.DidCommit);

        var history = RunGitCapture(_root, "log", "--follow", "--format=%B%x00", "--", Relative(codeReview))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Contains("Run-Index:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, m => m.Contains("Run-Index: 1") && m.Contains("Verdict: reissue"));
        Assert.Contains(history, m => m.Contains("Run-Index: 2") && m.Contains("Verdict: accept"));
        Assert.Contains(history, m => m.Contains("Steps: aspect-code-quality=pass"));
    }

    [Fact]
    public void RunBoundaryCommit_UsesPipelineAttemptForRunIndex()
    {
        var job = JobFolder("ASS-8");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "code-review.md"), "review\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Pass", attempt: 2));
        File.WriteAllText(Path.Combine(job, "logs", "session-events.jsonl"), "{}\n{}\n{}\n{}\n");

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-8",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.AcceptAsDone);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(2, result.RunIndex);

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Run-Index: 2", message);
        Assert.Contains("Verdict: accept", message);
    }

    [Fact]
    public void RunBoundaryCommit_ParsesNumericPipelineStatusesForStepsTrailer()
    {
        var job = JobFolder("ASS-9");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), NumericPipelineJson());

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-9",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.Reissue);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(
            "pre-loop-guard=passed,aspect-code-quality=warn,post-orchestrator-decision=skipped",
            result.Steps);

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Steps: pre-loop-guard=passed,aspect-code-quality=warn,post-orchestrator-decision=skipped", message);
    }

    [Fact]
    public void RunBoundaryCommit_EnqueuesEveryCommitForImmediatePush()
    {
        var queue = new WorkspaceArtifactPushQueue();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var service = new WorkspaceArtifactCommitService(
            config, NullLogger<WorkspaceArtifactCommitService>.Instance, queue);
        var job = JobFolder("ASS-PUSH");
        Directory.CreateDirectory(job);

        File.WriteAllText(Path.Combine(job, "status.md"), "one\n");
        Assert.True(service.TryCommitArtifactUpload(_root, "ASS-PUSH", job, ["status.md"]).DidCommit);
        File.AppendAllText(Path.Combine(job, "status.md"), "two\n");
        Assert.True(service.TryCommitArtifactUpload(_root, "ASS-PUSH", job, ["status.md"]).DidCommit);

        Assert.True(queue.Reader.TryRead(out var first));
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("ASS-PUSH", first!.JobId);
        Assert.Equal("ASS-PUSH", second!.JobId);
    }

    [Fact]
    public async Task WorkspacePushWorker_FailureIsToleratedAfterBoundedRetries()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0"
            })
            .Build();
        var store = new AgentStudio.Bus.AgentMessageBusStore();
        var bus = new AgentStudio.Bus.AgentMessageBusBridge(
            store, config, NullLogger<AgentStudio.Bus.AgentMessageBusBridge>.Instance);
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus);

        var pushed = await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(_root, "ASS-OFFLINE"), default);

        Assert.False(pushed);
        var pushFailure = Assert.Single(store.Recent(_root, project: "workspace", limit: 10));
        Assert.Equal("workspace-repository-push-blocked", pushFailure.Topic);
        Assert.Equal("advisory", pushFailure.Kind);
        Assert.Equal("ASS-OFFLINE", pushFailure.JobId);
        Assert.Contains(_root, pushFailure.Body);
        Assert.Contains("Ahead count: 1", pushFailure.Body);
    }

    [Fact]
    public async Task WorkspacePushWorker_UsesCatchUpBudgetAfterTimeout()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
                ["WorkspaceArtifacts:PushTimeoutSeconds"] = "30",
                ["WorkspaceArtifacts:CatchUpPushTimeoutSeconds"] = "600",
            })
            .Build();
        var pushTimeouts = new List<TimeSpan>();
        var pushAttempts = 0;
        Task<GitProcessResult> Run(WorkspaceGitInvocation invocation, CancellationToken _)
        {
            if (invocation.Arguments[0] == "rev-list")
                return Task.FromResult(new GitProcessResult(
                    0,
                    invocation.Arguments.Contains("--count") ? "2000\n" : "209715200\n",
                    string.Empty,
                    GitProcessFailureKind.None));

            pushTimeouts.Add(invocation.Timeout);
            pushAttempts++;
            return Task.FromResult(pushAttempts == 1
                ? new GitProcessResult(
                    -1,
                    string.Empty,
                    "timed out",
                    GitProcessFailureKind.TimedOut)
                : new GitProcessResult(0, string.Empty, string.Empty, GitProcessFailureKind.None));
        }

        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus: null,
            Run);

        var pushed = await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(_root, "ASS-CATCH-UP"),
            default);

        Assert.True(pushed);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)], pushTimeouts);
    }

    [Theory]
    [InlineData(49, 99, false)]
    [InlineData(50, 99, true)]
    [InlineData(1, 100, true)]
    public void WorkspacePushRetryPolicy_WarnsAtEitherBacklogThreshold(
        long ahead,
        long bytes,
        bool expected) =>
        Assert.Equal(expected, WorkspacePushRetryPolicy.ShouldWarn(ahead, bytes, 50, 100));

    [Fact]
    public async Task WorkspacePushWorker_IsSingleFlightPerRepository()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
            })
            .Build();
        var releaseFirstPush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pushCalls = 0;
        var activePushes = 0;
        var maximumActivePushes = 0;

        async Task<GitProcessResult> Run(WorkspaceGitInvocation invocation, CancellationToken _)
        {
            if (invocation.Arguments[0] == "rev-list")
                return new GitProcessResult(
                    0,
                    "1\n",
                    string.Empty,
                    GitProcessFailureKind.None);

            Interlocked.Increment(ref pushCalls);
            var active = Interlocked.Increment(ref activePushes);
            maximumActivePushes = Math.Max(maximumActivePushes, active);
            firstPushStarted.TrySetResult();
            await releaseFirstPush.Task;
            Interlocked.Decrement(ref activePushes);
            return new GitProcessResult(0, string.Empty, string.Empty, GitProcessFailureKind.None);
        }

        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus: null,
            Run);

        var first = worker.ProcessAsync(new WorkspaceArtifactPushRequest(_root, "first"), default);
        await firstPushStarted.Task;
        var second = worker.ProcessAsync(new WorkspaceArtifactPushRequest(_root, "second"), default);

        Assert.Equal(1, Volatile.Read(ref pushCalls));
        releaseFirstPush.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, maximumActivePushes);
        Assert.Equal(2, pushCalls);
    }

    [Fact]
    public void TrackedDriftSweep_CommitsTrackedFilesAndBusButNeverAttemptAuthority()
    {
        var busDirectory = Path.Combine(_root, "logs", "bus", "_workspace");
        var metadata = Path.Combine(_root, ".metadata");
        Directory.CreateDirectory(busDirectory);
        Directory.CreateDirectory(metadata);
        var busFile = Path.Combine(busDirectory, "2026-09-06.jsonl");
        var tracked = Path.Combine(_root, "tracked.txt");
        var authority = Path.Combine(metadata, "attempt-authority.json");
        File.WriteAllText(busFile, "{\"event\":1}\n");
        File.WriteAllText(tracked, "before\n");
        File.WriteAllText(authority, "{\"attempts\":[]}\n");
        RunGit(_root, "add", "logs/bus", "tracked.txt", ".metadata/attempt-authority.json");
        RunGit(_root, "commit", "-q", "-m", "tracked fixtures");

        File.AppendAllText(busFile, "{\"event\":2}\n");
        File.WriteAllText(Path.Combine(busDirectory, "new.jsonl"), "{\"event\":3}\n");
        File.WriteAllText(tracked, "after\n");
        File.WriteAllText(authority, "{\"attempts\":[1]}\n");

        var result = _service.TryCommitTrackedSweep(_root);

        Assert.True(result.DidCommit, result.Error);
        var committed = RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Replace('\\', '/')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("tracked.txt", committed);
        Assert.Contains("logs/bus/_workspace/2026-09-06.jsonl", committed);
        Assert.Contains("logs/bus/_workspace/new.jsonl", committed);
        Assert.DoesNotContain(".metadata/attempt-authority.json", committed);
        Assert.Contains(".metadata/attempt-authority.json", RunGitCapture(_root, "status", "--short"));
        Assert.Empty(RunGitCapture(_root, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public void RunBoundaryCommit_RefusesFileAboveFiftyMegabytes()
    {
        var job = JobFolder("ASS-LARGE");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "status.md"), "small evidence\n");
        var oversized = Path.Combine(job, "oversized-evidence.bin");
        using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(50L * 1024 * 1024 + 1);

        var result = _service.TryCommitArtifactUpload(
            _root,
            "ASS-LARGE",
            job,
            ["status.md", "oversized-evidence.bin"]);

        Assert.True(result.DidCommit, result.Error);
        var committed = RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Replace('\\', '/');
        Assert.Contains("ASS-LARGE/status.md", committed);
        Assert.DoesNotContain("oversized-evidence.bin", committed);
        Assert.Contains("oversized-evidence.bin", RunGitCapture(_root, "status", "--short"));
        Assert.Empty(RunGitCapture(_root, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task WorkspacePushWorker_PushesTheRequestedShaToTheRequestedBranch()
    {
        var firstSha = RunGitCapture(_root, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_root, "later.txt"), "later\n");
        RunGit(_root, "add", "later.txt");
        RunGit(_root, "commit", "-q", "-m", "later");
        var remote = Path.Combine(_root, "remote.git");
        RunGit(_root, "init", "-q", "--bare", remote);
        RunGit(_root, "remote", "add", "origin", remote);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
            })
            .Build();
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config);

        var pushed = await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(
                _root,
                "service-mutation",
                TargetBranch: "develop",
                Sha: firstSha,
                Project: "Project"),
            default);

        Assert.True(pushed);
        Assert.Equal(
            firstSha,
            RunGitCapture(_root, $"--git-dir={remote}", "rev-parse", "refs/heads/develop").Trim());
    }

    private string JobFolder(string id) =>
        Path.Combine(_root, "projects", "agent-taskboard", "tasks", "001", id);

    private string Relative(string path) =>
        Path.GetRelativePath(_root, path).Replace('\\', '/');

    private static string PipelineJson(string stepId, string verdict, int? attempt = null)
    {
        var attemptLine = attempt.HasValue
            ? $",\n  \"attempt\": {attempt.Value}"
            : string.Empty;
        return
            "{\n" +
            "  \"steps\": [\n" +
            $"    {{ \"stepId\": \"{stepId}\", \"status\": \"Passed\", \"verdict\": \"{verdict}\" }}\n" +
            "  ]" +
            attemptLine + "\n" +
            "}\n";
    }

    private static string NumericPipelineJson() =>
        """
        {
          "steps": [
            { "stepId": "pre-loop-guard", "status": 2 },
            { "stepId": "core-agent-run", "status": 1 },
            { "stepId": "aspect-code-quality", "status": 2, "verdict": "Warn" },
            { "stepId": "post-orchestrator-decision", "status": 4 },
            { "stepId": "post-git-commit-attribution", "status": 5 }
          ],
          "attempt": 1
        }
        """;

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
        return result.Out;
    }

    private static (string Out, string Err, int Code) RunGitResult(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (stdout, stderr, p.ExitCode);
    }
}
