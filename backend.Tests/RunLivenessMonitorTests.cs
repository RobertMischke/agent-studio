using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Run-Liveness Slice A executor (concept:
/// docs/concepts/run-liveness-and-slot-semantics.md). Proves the invariant
/// "no zombie survives 60s" end to end against a real workspace:
///
/// <list type="number">
///   <item>Boot adoption: an execution zombie (no live heartbeat, run never
///   finished) is demoted 3-progress -&gt; 2-ready AND its session-resume pointer
///   is cleared, breaking the "No conversation found" launch-fail chain
///   (AGT-2006 / AGT-1945-1939).</item>
///   <item>Phase-aware recovery: a finished-but-post-processing-died card
///   (agent_run_finished present) is re-triggered to 4-auto-review, NOT demoted -
///   the completed agent run is never re-run (AGT-1932).</item>
///   <item>A card with a live pickup-lock owner keeps its heartbeat and is left
///   alone (a healthy foreign/own run is not stolen).</item>
///   <item>Uptime: a just-moved card inside the grace window is left alone.</item>
///   <item>No work lost: the demotion never tears down a worktree; the task
///   returns to 2-ready under its slug with its evidence intact.</item>
///   <item>Idempotency + the active-job defensive guard.</item>
/// </list>
/// </summary>
public sealed class RunLivenessMonitorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public RunLivenessMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-run-liveness-" + Guid.NewGuid().ToString("N"));
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
    public async Task BootAdoption_ExecutionZombie_DemotedToReady_AndResumePointerCleared()
    {
        // A run crashed with the backend mid-execution: 3-progress card, a stale
        // resume pointer, a dead pickup lock, no agent_run_finished signal.
        const string slug = "execution-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-abc", chain: new[] { "sess-abc" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent was mid-run when the backend died");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        // Demoted to 2-ready under its original slug (fresh run retries the task).
        Assert.False(Directory.Exists(folder), "zombie must leave 3-progress");
        var requeued = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(requeued), "interrupted execution run must return to 2-ready");

        var d = Assert.Single(outcomes);
        Assert.Equal(RunLivenessOutcomeKinds.DemotedProcessLost, d.Kind);
        Assert.Equal(RunLivenessReasons.ProcessLost, d.ReasonCode);
        Assert.Equal(TaskStates.Ready, d.TargetState);

        // The resume pointer is cleared: sessionName is emptied and the chain is
        // tombstoned so RunPlanner cannot re-derive the dead id and walk into the
        // "No conversation found" / "no rollout found" launch-fail chain.
        var (sessionName, chain) = ReadSession(Path.Combine(requeued, "task.json"));
        Assert.True(string.IsNullOrEmpty(sessionName), "sessionName must be cleared on process-lost demotion");
        Assert.Equal("(recovery)", chain[^1]);

        // The demotion is never silent: one compact recovery line travels along.
        var log = File.ReadAllText(Path.Combine(requeued, "logs", "cli-output.log"));
        Assert.Contains($"[{RecoveryChatLine.RecoveryTag}]", log);
        Assert.Contains($"requeued to {TaskStates.Ready}", log);
        Assert.Contains("session new", log);

        // Audit row lands in <workspace>/logs/run-liveness.jsonl.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl"));
        Assert.Contains("demoted-process-lost", jsonl);
        Assert.Contains("\"slug\":\"execution-zombie\"", jsonl);
    }

    [Fact]
    public async Task BootAdoption_FinishedRun_RetriggersPostProcessing_NotDemoted()
    {
        // AGT-1932: the agent run finished (and was merged) and only
        // post-processing died with the backend. The card must NOT be demoted to
        // 2-ready (that would re-run the completed agent) - it is re-triggered to
        // 4-auto-review where post-processing resumes.
        const string slug = "finished-postprocessing-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-done", chain: new[] { "sess-done" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent finished; post-processing was running");
        WriteAgentRunFinishedTimeline(folder);
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.False(Directory.Exists(folder), "card must leave 3-progress");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, slug)),
            "a finished run must NOT be demoted to 2-ready (that would re-run the completed agent)");
        var promoted = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Assert.True(Directory.Exists(promoted), "a finished run must be re-triggered into 4-auto-review");

        var d = Assert.Single(outcomes);
        Assert.Equal(RunLivenessOutcomeKinds.RetriggeredPostProcessing, d.Kind);
        Assert.Equal(RunLivenessReasons.PostProcessingLost, d.ReasonCode);
        Assert.Equal(TaskStates.AutoReview, d.TargetState);

        // The resume pointer is preserved for a finished run (not tombstoned).
        var (sessionName, _) = ReadSession(Path.Combine(promoted, "task.json"));
        Assert.Equal("sess-done", sessionName);

        var log = File.ReadAllText(Path.Combine(promoted, "logs", "cli-output.log"));
        Assert.Contains("[post-processing-recovered]", log);

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl"));
        Assert.Contains("retriggered-post-processing", jsonl);
    }

    [Fact]
    public async Task LiveHeartbeat_CardWithLivePickupLock_IsLeftAlone()
    {
        // A live pickup-lock owner (this process's pid) is a live run-heartbeat;
        // the card is healthy even though it is silent. This is what stops a
        // healthy run (own or a foreign backend sharing the workspace) being
        // stolen, and a healthy post-processing card being demoted.
        const string slug = "healthy-live-run";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-live", chain: new[] { "sess-live" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "agent working");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log")); // silent, but owned
        WriteLivePickupLock(folder);

        var (monitor, _) = Build();
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "a card with a live heartbeat must never be touched");
        Assert.Empty(outcomes); // healthy verdicts are not persisted as actions
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")));
    }

    [Fact]
    public async Task Uptime_FreshCard_WithinGrace_IsLeftAlone()
    {
        // During uptime a card can be heartbeat-less for a beat between the lane
        // move and the run claim. A card whose last activity is inside the grace
        // window must not be demoted yet (it is re-checked next tick).
        const string slug = "just-moved";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: null, chain: Array.Empty<string>());
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "just entered 3-progress"); // mtime = now, inside grace
        // No pickup lock yet (the claim/lock has not landed).

        var (monitor, _) = Build();
        var outcomes = await monitor.SweepAsync(); // uptime path applies the grace

        Assert.True(Directory.Exists(folder), "a fresh card inside the grace must not be demoted");
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task Demotion_PreservesEvidence_ReturnsSameTaskToReady()
    {
        // AGT-1945 "never lose work": the monitor never tears down a worktree, so
        // demotion cannot drop the deliverable. The task returns to 2-ready under
        // its slug carrying its evidence, ready for a clean reissue that reuses
        // the task-owned worktree.
        const string slug = "keep-the-work";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "sess-x", chain: new[] { "sess-x" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "produced partial work before the crash");
        File.WriteAllText(Path.Combine(folder, "results-note.md"), "partial deliverable");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        await monitor.AdoptOnBootAsync();

        var requeued = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Assert.True(Directory.Exists(requeued));
        Assert.True(File.Exists(Path.Combine(requeued, "task.json")), "task.json must survive the demotion");
        Assert.True(File.Exists(Path.Combine(requeued, "results-note.md")), "evidence must travel with the demoted folder");
        Assert.True(File.Exists(Path.Combine(requeued, "logs", "cli-output.log")), "logs must survive the demotion");
    }

    [Fact]
    public async Task AdoptOnBoot_IsIdempotentAcrossRuns()
    {
        const string slug = "one-shot-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "no heartbeat");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build();
        var first = await monitor.AdoptOnBootAsync();
        Assert.Single(first);

        var len1 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")).Length;

        var second = await monitor.AdoptOnBootAsync();
        Assert.Empty(second); // no candidates remain in 3-progress

        var len2 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "run-liveness.jsonl")).Length;
        Assert.Equal(len1, len2); // no new audit lines on the rerun
    }

    [Fact]
    public async Task ActiveJob_IsNeverTouched_ViaStatusOverride()
    {
        // The defensive guard: even a silent, lock-less 3-progress card that is
        // the runner's declared active job is never demoted (it is being run by
        // this backend right now).
        const string slug = "running-now";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "mid-stream");
        SetMtimeOld(Path.Combine(folder, "logs", "cli-output.log"));

        var (monitor, _) = Build();
        monitor.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus
                {
                    ProjectName = ProjectName,
                    Mode = "auto-continuous",
                    ActiveJobId = slug
                }
            }
        };

        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "the active job must never be demoted");
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task Disabled_SkipsScanEntirely()
    {
        const string slug = "would-be-zombie";
        WriteJobWithSession(TaskStates.Progress, slug, sessionName: "s", chain: new[] { "s" });
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        WriteCliLog(folder, "no heartbeat");
        WriteDeadPickupLock(folder);

        var (monitor, _) = Build(enabled: false);
        var outcomes = await monitor.AdoptOnBootAsync();

        Assert.True(Directory.Exists(folder), "with the monitor disabled nothing moves");
        Assert.Empty(outcomes);
    }

    // ---- harness ---------------------------------------------------------

    private (RunLivenessMonitor Monitor, TaskScannerService Scanner) Build(bool enabled = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Runner:RunLiveness:Enabled"] = enabled ? "true" : "false",
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var pickupLock = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var sp = new ServiceCollection().BuildServiceProvider();

        var monitor = new RunLivenessMonitor(
            scanner, transitions, sessions, pickupLock, chatLog, sp, config, taskAccess,
            NullLogger<RunLivenessMonitor>.Instance);
        return (monitor, scanner);
    }

    private void WriteJobWithSession(string state, string slug, string? sessionName, string[] chain)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var sessionJson = sessionName == null ? "null" : JsonSerializer.Serialize(sessionName);
        var chainJson = JsonSerializer.Serialize(chain);
        var enteredLaneAt = (DateTime.UtcNow - TimeSpan.FromMinutes(10)).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"," +
            $"\"sessionName\":{sessionJson},\"sessionChain\":{chainJson},\"enteredLaneAt\":\"{enteredLaneAt}\"}}");
    }

    private static void WriteCliLog(string folder, string body)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cli-output.log"),
            $"[12:00:00.000] [stdout] {body}{Environment.NewLine}");
    }

    private static void WriteAgentRunFinishedTimeline(string folder)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "timeline.jsonl"),
            "{\"Ts\":\"2026-07-10T00:00:00Z\",\"Kind\":\"agent_run_finished\",\"Actor\":\"agent\",\"Summary\":\"claude run completed\"}" + Environment.NewLine);
    }

    private void WriteLivePickupLock(string folder) => WritePickupLock(folder, System.Environment.ProcessId);

    // A pid that is (practically) certain not to be a running process on this
    // host, so the same-host liveness probe reads the lock as reclaimable.
    private void WriteDeadPickupLock(string folder) => WritePickupLock(folder, 2_000_000_000);

    private void WritePickupLock(string folder, int pid)
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        lockFile.TryAcquire(folder, new PickupLockOwner
        {
            Pid = pid,
            Hostname = System.Environment.MachineName,
            Role = "primary",
            BackendName = "test-backend",
            ProjectName = ProjectName,
            JobId = Path.GetFileName(folder),
        }, out _);
    }

    private static void SetMtimeOld(string path)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(10));
    }

    private static (string? SessionName, List<string> Chain) ReadSession(string jobJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
        var root = doc.RootElement;
        string? name = root.TryGetProperty("sessionName", out var sn) && sn.ValueKind == JsonValueKind.String
            ? sn.GetString() : null;
        var chain = new List<string>();
        if (root.TryGetProperty("sessionChain", out var sc) && sc.ValueKind == JsonValueKind.Array)
            foreach (var el in sc.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) chain.Add(el.GetString()!);
        return (name, chain);
    }
}
