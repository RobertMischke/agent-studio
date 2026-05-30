using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Boot-time stale-progress sweep (pairs with ADR-0020 crash recovery; routing
/// per ADR-0051 failed-pickup elimination, supersedes ADR-0028/0029). Five cases
/// plus the active-job defensive guard:
///
/// <list type="number">
///   <item>Sentinel + stale -> finished missed transition into 4-auto-review
///   with a <c>recovered-from-stuck-progress</c> supervisor chat note.</item>
///   <item>No sentinel + stale + has <c>job.json</c> -> requeued to
///   <c>2-ready</c> so the pickup loop retries the same task (an interrupted run
///   is not a failure). No new orphan card.</item>
///   <item>Empty + stale + no <c>job.json</c> -> archived to <c>7-archive</c> as
///   <c>-debris-&lt;date&gt;</c> (debris, not a runnable task).</item>
///   <item>Fresh -> untouched (progress-first pickup will resume).</item>
///   <item>Re-run on the same lane -> no further changes (idempotency).</item>
///   <item>Active job -> never touched even when stale (defensive guard).</item>
/// </list>
/// </summary>
public sealed class StaleProgressArchiverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public StaleProgressArchiverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-stale-progress-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
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

    [Fact]
    public async Task Sweep_StaleFolderWithDoneSentinel_RecoversToReviewAndAppendsChatNote()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "demo-task");
        WriteCliLogWithSentinel(folder, "[[TASK_DONE]]");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, "demo-task");
        Assert.False(Directory.Exists(folder), "source 3-progress folder must be moved");
        Assert.True(Directory.Exists(moved), "job folder must land in 4-review");

        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RecoveredToReview, d.Kind);
        Assert.Equal("DONE", d.SentinelKeyword);
        Assert.Equal(TaskStates.AutoReview, d.TargetState);

        // Chat-log note lands on the moved folder so the protocol pane sees it.
        var log = File.ReadAllText(Path.Combine(moved, "logs", "cli-output.log"));
        Assert.Contains("[recovered-from-stuck-progress]", log);
        Assert.Contains("[supervisor]", log);

        // Decision lands in <workspace>/logs/orphan-recoveries.jsonl.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("recovered-to-review", jsonl);
        Assert.Contains("\"slug\":\"demo-task\"", jsonl);
    }

    [Fact]
    public async Task Sweep_StaleFolderWithJobJsonNoSentinel_IsRequeuedToReadyNotDeadLettered()
    {
        // ADR-0051 (failed-pickup elimination): a stale 3-progress folder that
        // still carries a job.json is a real task whose run was interrupted, not
        // a task that failed. It is requeued to 2-ready so the pickup loop
        // retries the same task. No new orphan card, nothing in failed-pickup.
        WriteJob(TaskStates.Progress, "no-sentinel");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "no-sentinel");
        WriteCliLog(folder, "agent talked but never finished");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder), "source 3-progress folder must be moved");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RequeuedToReady, d.Kind);
        Assert.Equal(TaskStates.Ready, d.TargetState);

        // The same task returns to 2-ready under its original slug.
        var requeued = Path.Combine(_watchPath, TaskStates.Ready, "no-sentinel");
        Assert.True(Directory.Exists(requeued), "interrupted task must return to 2-ready under its original slug");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup, "no-sentinel")),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        // A supervisor chat note travels with the folder so the requeue is never silent.
        var log = File.ReadAllText(Path.Combine(requeued, "logs", "cli-output.log"));
        Assert.Contains("retries the same task", log);

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("requeued-to-ready", jsonl);
        Assert.DoesNotContain("moved-to-failed-pickup", jsonl);
    }

    [Fact]
    public async Task Sweep_EmptyStaleFolderNoJobJson_IsArchivedAsDebrisNotDeadLettered()
    {
        // ADR-0051 (failed-pickup elimination): an empty stale folder with no
        // job.json is not a runnable task. It is debris and is archived to
        // 7-archive with its evidence intact, never parked in a dead-end lane.
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "empty-shell");
        Directory.CreateDirectory(folder);
        // No job.json, no logs. MeasureFolder treats this as epoch 0 so it
        // always crosses the threshold.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder));
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.ArchivedDebris, d.Kind);
        Assert.Equal(TaskStates.Archive, d.TargetState);
        Assert.NotNull(d.FailedPickupSlug);
        Assert.StartsWith("empty-shell-debris-", d.FailedPickupSlug);

        var archived = Path.Combine(_watchPath, TaskStates.Archive, d.FailedPickupSlug!);
        Assert.True(Directory.Exists(archived), "debris must land in 7-archive");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup, d.FailedPickupSlug!)),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("archived-debris", jsonl);
        Assert.DoesNotContain("moved-to-failed-pickup", jsonl);
    }

    [Fact]
    public async Task Sweep_FreshFolder_IsLeftAlone()
    {
        WriteJob(TaskStates.Progress, "fresh");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "fresh");
        WriteCliLog(folder, "still working");
        // mtime stays "now" so the folder is well within the resume window.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "fresh folder must not be moved");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Fresh, d.Kind);

        // Fresh verdicts are not persisted in orphan-recoveries.jsonl.
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")));
    }

    [Fact]
    public async Task Sweep_StaleCliLogButFreshToolCalls_IsLeftAlone()
    {
        // Regression guard for the suchbox-orphan incident (2026-05-07): a
        // claude-code session emitted only tool-use events into
        // logs/tool-calls.jsonl for tens of minutes while logs/cli-output.log
        // stayed quiet. Reading cli-output.log alone misclassified the live
        // folder as orphan and the sweep moved it. The activity signature now
        // spans every file in logs/, so a fresh tool-calls.jsonl keeps the
        // verdict at Fresh.
        WriteJob(TaskStates.Progress, "tool-calling");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "tool-calling");
        WriteCliLog(folder, "long-quiet stdout");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        // tool-calls.jsonl mtime defaults to "now" since we just wrote it.
        var toolCalls = Path.Combine(folder, "logs", "tool-calls.jsonl");
        File.WriteAllText(toolCalls, "{\"ts\":\"now\",\"kind\":\"started\",\"tool\":\"Bash\"}\n");

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "fresh tool-calls.jsonl must keep the folder alive");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Fresh, d.Kind);
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")));
    }

    [Fact]
    public async Task Sweep_StaleCliLogButFreshSessionEvents_IsLeftAlone()
    {
        // Sister case to the tool-calls path: the runner writes a one-line
        // start/continue event into logs/session-events.jsonl at every
        // pickup attempt. A folder where session-events.jsonl was just
        // appended must count as fresh even when cli-output.log mtime is
        // stale (e.g. claude-code session emitted no stdout yet).
        WriteJob(TaskStates.Progress, "just-resumed");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "just-resumed");
        WriteCliLog(folder, "old stdout from a previous attempt");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var sessionEvents = Path.Combine(folder, "logs", "session-events.jsonl");
        File.WriteAllText(sessionEvents, "{\"Ts\":\"now\",\"Kind\":\"continue\",\"Cli\":\"claude\"}\n");

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Equal(StaleProgressDecisionKinds.Fresh, Assert.Single(decisions).Kind);
    }

    [Fact]
    public async Task Sweep_IsIdempotentAcrossRuns()
    {
        WriteJob(TaskStates.Progress, "first-orphan");
        var f1 = Path.Combine(_watchPath, TaskStates.Progress, "first-orphan");
        WriteCliLog(f1, "no sentinel here");
        SetMtimeOldEnough(Path.Combine(f1, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f1, "job.json"));

        WriteJob(TaskStates.Progress, "second-recovered");
        var f2 = Path.Combine(_watchPath, TaskStates.Progress, "second-recovered");
        WriteCliLogWithSentinel(f2, "[[TASK_NEEDS_INPUT:waiting]]");
        SetMtimeOldEnough(Path.Combine(f2, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f2, "job.json"));

        var (archiver, _) = Build();
        var first = await archiver.SweepAsync();
        Assert.Equal(2, first.Count);

        var jsonlLen1 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;

        var second = await archiver.SweepAsync();
        Assert.Empty(second); // no candidates remain in 3-progress

        var jsonlLen2 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;
        Assert.Equal(jsonlLen1, jsonlLen2); // no new lines on the rerun
    }

    [Fact]
    public async Task Sweep_ActiveJobIsNeverTouchedEvenWhenStale()
    {
        WriteJob(TaskStates.Progress, "running-now");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "running-now");
        WriteCliLog(folder, "agent mid-stream");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        archiver.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus
                {
                    ProjectName = ProjectName,
                    Mode = "auto-continuous",
                    ActiveJobId = "running-now"
                }
            }
        };

        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "active job folder must never be moved by the sweep");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Skipped, d.Kind);
    }

    [Fact]
    public async Task Sweep_ZeroWindow_DisablesPass()
    {
        WriteJob(TaskStates.Progress, "would-be-orphan");
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "would-be-orphan");
        WriteCliLog(folder, "no sentinel");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build(stuckResumeWindowMinutes: 0);
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Empty(decisions);
    }

    private (StaleProgressArchiver Archiver, TaskScannerService Scanner) Build(int stuckResumeWindowMinutes = 60)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Supervisor:StuckResumeWindowMinutes"] = stuckResumeWindowMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        // Empty service provider: tests use StatusProviderOverride to drive the
        // active-job guard, so the runner doesn't need to be instantiated.
        var sp = new ServiceCollection().BuildServiceProvider();

        var archiver = new StaleProgressArchiver(
            scanner, states, transitions, chatLog, sp, config, taskAccess,
            NullLogger<StaleProgressArchiver>.Instance);
        return (archiver, scanner);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static void WriteCliLog(string folder, string body)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cli-output.log"),
            $"[12:00:00.000] [stdout] {body}{Environment.NewLine}");
    }

    private static void WriteCliLogWithSentinel(string folder, string sentinel)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        for (int i = 0; i < 10; i++) lines.Add($"[12:0{i}:00.000] [stdout] working line {i}");
        lines.Add($"[12:30:00.000] [stdout] {sentinel}");
        File.WriteAllText(Path.Combine(dir, "cli-output.log"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void SetMtimeOldEnough(string path)
    {
        // Three hours back keeps us well past the 60-minute default window.
        var stale = DateTime.UtcNow - TimeSpan.FromHours(3);
        File.SetLastWriteTimeUtc(path, stale);
    }
}
