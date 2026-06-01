using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Endpoints.Tasks;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression test for the multi-second lag the user hit on /api/tasks and
/// /api/tasks/grouped after the auto-loop snapshot was folded onto every
/// TaskInfo.
///
/// <para>
/// Root cause that this test pins down: <c>WithRuntime</c> looked up the
/// auto-loop state via <c>TaskRunnerService.GetStuckLoopStateForJob(jobId,
/// watchPath)</c>, which called <c>TaskScannerService.FindJob</c>, which
/// performed a full <c>ScanAllJobs</c> (disk walk + JSON parse) on every
/// invocation. With ~150 jobs that meant the grouped endpoint did 150
/// full disk rescans per HTTP call, taking 7-15 seconds. The frontend
/// polls grouped jobs every 5 seconds, so the UI was permanently
/// blocked behind the previous poll.
/// </para>
///
/// <para>
/// The contract being locked: enriching N TaskInfos with WithRuntime must
/// be O(N) in cheap in-memory lookups, with no per-job disk I/O. We
/// assert the runtime overlay phase completes in &lt; 1 second on a
/// realistic board of 200 jobs. That ceiling is generous (the real fix
/// brings it under 50 ms); we leave headroom for a slow CI runner.
/// </para>
/// </summary>
public class JobsEndpointPerfTests : IDisposable
{
    private readonly string _watchPath;

    public JobsEndpointPerfTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-perf-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WithRuntime_Over200Jobs_FinishesWellUnderOneSecond()
    {
        // Arrange — populate the watch path with 200 jobs split across the
        // archive lane (the bulk of a real-world board accumulates there).
        const int jobCount = 200;
        const string projectName = "perf-test";
        for (var i = 0; i < jobCount; i++)
        {
            WriteJob(TaskStates.Archive, $"job-{i:D4}");
        }

        var (router, runners) = BuildRuntime(projectName);
        var scanner = BuildScanner();

        // Warm one scan so the JIT / file system cache are settled — we are
        // measuring the overlay, not the first-touch cost.
        var jobs = scanner.ScanAllJobs();
        Assert.Equal(jobCount, jobs.Count);
        _ = jobs.Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();

        // Act — measure the overlay only. Even if ScanAllJobs gets faster
        // later, the regression we are guarding against was inside the
        // overlay (per-job FindJob causing a full rescan).
        var sw = Stopwatch.StartNew();
        var enriched = jobs.Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();
        sw.Stop();

        // Assert — generous ceiling. The pre-fix path took ~7-15s for 144
        // jobs; the post-fix path is &lt; 50ms. 1000ms catches the regression
        // on any reasonable CI runner without flaking on slow ones.
        Assert.Equal(jobCount, enriched.Count);
        Assert.True(
            sw.ElapsedMilliseconds < 1000,
            $"WithRuntime over {jobCount} jobs took {sw.ElapsedMilliseconds} ms; " +
            "the auto-loop / summary lookups must be O(1) per job and never re-scan disk. " +
            "If this assertion fires, look at TaskRunnerService.GetStuckLoopStateForJob and " +
            "any other helper that might be calling TaskScannerService.FindJob inside the " +
            "per-job overlay loop.");
    }

    [Fact]
    public void BuildTokenLookup_UsesCanonicalAggregatorOncePerProject()
    {
        var jobs = new[]
        {
            MakeJob("job-a", "project-a", Path.Combine(_watchPath, "a")),
            MakeJob("job-b", "project-a", Path.Combine(_watchPath, "a")),
            MakeJob("job-a", "project-b", Path.Combine(_watchPath, "b")),
        };
        var tokens = new FakeTokenAggregator(new Dictionary<string, Dictionary<string, TaskTokenSummary>>(StringComparer.OrdinalIgnoreCase)
        {
            ["project-a"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new TaskTokenSummary { TotalTokens = 10 },
                ["job-b"] = new TaskTokenSummary { TotalTokens = 20 },
            },
            ["project-b"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new TaskTokenSummary { TotalTokens = 30 },
            },
        });

        var lookup = TaskEndpointHelpersAccessor.BuildTokenLookup(jobs, tokens);

        Assert.Equal(2, tokens.Calls.Count);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-a" && c.WatchPath == jobs[0].WatchPath);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-b" && c.WatchPath == jobs[2].WatchPath);
        Assert.Equal(10, lookup[jobs[0].TaskKey].TotalTokens);
        Assert.Equal(20, lookup[jobs[1].TaskKey].TotalTokens);
        Assert.Equal(30, lookup[jobs[2].TaskKey].TotalTokens);
    }

    [Fact]
    public void WithRuntime_OutsideProgress_ClearsExecutionOverlay()
    {
        // Single-source-of-truth contract (Lane > Execution-Status >
        // Default): the wire overlay may only surface a CLI Execution
        // snapshot for a task that is actually in 3-progress. A task that
        // has moved on to 4-auto-review / 5-human-review / 6-completed /
        // 7-archive must come back with Execution == null even when the CLI
        // driver still holds a live "running" snapshot for that TaskKey —
        // the driver retains a ProcInfo ~30 min post-exit, and a foreign
        // backend can keep one alive across a move. Without the lane gate
        // the per-card pill renders that stale snapshot as a misleading
        // "Running" badge on a card that is not executing in this lane.
        const string projectName = "lane-gate";
        var (router, runners) = BuildRuntime(projectName);

        // Swap the Claude driver (the router's default route) for a fake
        // that always reports a live "running" execution, so the assertion
        // exercises the state gate rather than an empty _processes dict that
        // would return null for every lane anyway.
        var sentinel = new CliExecution
        {
            JobId = "task-7",
            TaskKey = $"{_watchPath}::task-7",
            ProcessId = 4242,
            StartedAt = DateTime.UtcNow,
            Status = "running",
        };
        InjectExecutionDriver(router, sentinel);

        TaskInfo At(string state) => MakeJob("task-7", projectName, _watchPath) with
        {
            State = state,
            CliType = CliTypes.Claude,
        };

        // 3-progress → overlay surfaces the live running snapshot.
        var progress = TaskEndpointHelpersAccessor.WithRuntime(At(TaskStates.Progress), router, runners);
        Assert.NotNull(progress.Execution);
        Assert.Equal("running", progress.Execution!.Status);

        // Every lane past 3-progress → overlay clears it to null.
        foreach (var state in new[] { TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Completed, TaskStates.Archive })
        {
            var enriched = TaskEndpointHelpersAccessor.WithRuntime(At(state), router, runners);
            Assert.True(
                enriched.Execution is null,
                $"Execution must be null for state '{state}', but the overlay surfaced status '{enriched.Execution?.Status}'.");
        }
    }

    private static void InjectExecutionDriver(CliRouter router, CliExecution execution)
    {
        // CliRouter._byType is the private cli-type → driver map consulted by
        // Get(). Replace the Claude entry (matches CliType = "claude" jobs)
        // with a fake so GetExecution returns a known live snapshot.
        var field = typeof(CliRouter).GetField("_byType",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var byType = (System.Collections.IDictionary)field.GetValue(router)!;
        byType[CliTypes.Claude] = new FakeRunningCliService(execution);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static TaskInfo MakeJob(string id, string projectName, string watchPath) => new()
    {
        Id = id,
        TaskKey = $"{watchPath}::{id}",
        Title = id,
        State = TaskStates.Progress,
        ProjectName = projectName,
        WatchPath = watchPath,
        FolderPath = Path.Combine(watchPath, TaskStates.Progress, id),
    };

    private TaskScannerService BuildScanner()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private (CliRouter router, TaskRunnerService runners) BuildRuntime(string projectName)
    {
        var config = BuildConfig(projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);

        // Minimal CliRouter wired with all four drivers - the overlay only
        // calls router.Get(...).GetExecution(), which returns null when no
        // process is registered. That's exactly what we want for the perf
        // assertion: a fast no-op lookup.
        var cliEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var copilot = new CopilotCliService(
            NullLogger<CopilotCliService>.Instance, config,
            new CopilotModelDiscovery(NullLogger<CopilotModelDiscovery>.Instance, cliEnv, config),
            cliEnv);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var claude = new ClaudeCliService(NullLogger<ClaudeCliService>.Instance, config);
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, config);
        var router = new CliRouter(copilot, claude, codex, gemini);

        var contextUsageParser = new ContextUsageParser();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var projectSettings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, projectSettings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, scanner, config);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        var runners = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            copilot, router, contextUsageParser, summary, prompts, transitions, projectSettings,
            quotaService, quotaCaps,
            chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot, git, pickupFailures, infraBreaker, taskAccess);
        return (router, runners);
    }

    private IConfiguration BuildConfig(string projectName = "perf-test")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
    }
}

internal sealed class FakeTokenAggregator : ITokenAggregator
{
    private readonly IReadOnlyDictionary<string, Dictionary<string, TaskTokenSummary>> _perProject;
    public List<(string ProjectName, string WatchPath)> Calls { get; } = [];

    public FakeTokenAggregator(IReadOnlyDictionary<string, Dictionary<string, TaskTokenSummary>> perProject)
    {
        _perProject = perProject;
    }

    public TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default) => throw new NotImplementedException();
    public ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null) => throw new NotImplementedException();
    public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null) => throw new NotImplementedException();
    public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit) => throw new NotImplementedException();
    public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId) => throw new NotImplementedException();
    public TokenSummary LifetimeSummary(string projectName, string watchPath) => throw new NotImplementedException();
    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects) => throw new NotImplementedException();
    public TokenSummaryAggregate? CachedWorkspaceAggregate() => throw new NotImplementedException();
    public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null) => throw new NotImplementedException();
    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null) => throw new NotImplementedException();

    public Dictionary<string, TaskTokenSummary> WorkspacePerJob(string projectName, string watchPath)
    {
        Calls.Add((projectName, watchPath));
        return _perProject.TryGetValue(projectName, out var perJob)
            ? perJob
            : new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
    }
}

/// <summary>
/// Minimal <see cref="ICliExecutionService"/> stub that reports a fixed live
/// execution for any key. Only <see cref="CliType"/> and
/// <see cref="GetExecution"/> are exercised by the wire overlay; every other
/// member throws so an accidental call shows up loudly rather than silently
/// returning a default.
/// </summary>
internal sealed class FakeRunningCliService : ICliExecutionService
{
    private readonly CliExecution _execution;
    public FakeRunningCliService(CliExecution execution) => _execution = execution;

    public string CliType => CliTypes.Claude;
    public CliExecution? GetExecution(string jobKey) => _execution;

    public string GetCliPath() => throw new NotImplementedException();
    public bool IsAvailable() => throw new NotImplementedException();
    public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => throw new NotImplementedException();
    public Task<(CliExecution? Execution, string? Error)> StartAsync(string jobId, string jobKey, string prompt, string workingDirectory, string? sessionName = null, bool resumeSession = false, string? model = null, string? jobFolderPath = null, CancellationToken ct = default) => throw new NotImplementedException();
    public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop) => throw new NotImplementedException();
    public bool SendInput(string jobKey, string input) => throw new NotImplementedException();
    public List<CliOutputLine> GetOutput(string jobKey) => throw new NotImplementedException();
    public void DiscardPersistedOutput(string jobKey) => throw new NotImplementedException();
    public SessionUsage? GetLastUsage(string jobKey) => throw new NotImplementedException();
    public bool IsRunningForProject(string rootPath) => throw new NotImplementedException();
    public DateTime? GetLastStreamedAt(string jobKey) => throw new NotImplementedException();
    public WatchdogState GetWatchdogState(string jobKey) => throw new NotImplementedException();
    public void SetWatchdogState(string jobKey, WatchdogState state) => throw new NotImplementedException();
    public void ReattachOnStartup() { }
    public Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default) => throw new NotImplementedException();
    public bool IsCompatibleSessionName(string? sessionName) => throw new NotImplementedException();

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;
    public event Action<string, CliRunEvent>? OnRunEvent;
}

/// <summary>
/// TaskEndpointHelpers.WithRuntime is internal; this thin accessor lets the
/// regression test reach it without making the helper public on its own.
/// Lives in the test project so the production surface stays unchanged.
/// </summary>
internal static class TaskEndpointHelpersAccessor
{
    public static TaskInfo WithRuntime(TaskInfo job, CliRouter router, TaskRunnerService runners)
    {
        // Reflection over the internal helper. Keeps the production access
        // modifier honest while still letting the test call into it.
        var t = typeof(OrchestratorApi.Endpoints.Tasks.TaskCrudEndpoints).Assembly
            .GetType("OrchestratorApi.Endpoints.Tasks.TaskEndpointHelpers")!;
        var m = t.GetMethod("WithRuntime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(TaskInfo), typeof(CliRouter), typeof(TaskRunnerService) })!;
        return (TaskInfo)m.Invoke(null, new object[] { job, router, runners })!;
    }

    public static Dictionary<string, TaskTokenSummary> BuildTokenLookup(IEnumerable<TaskInfo> jobs, ITokenAggregator tokens)
    {
        var t = typeof(OrchestratorApi.Endpoints.Tasks.TaskCrudEndpoints).Assembly
            .GetType("OrchestratorApi.Endpoints.Tasks.TaskEndpointHelpers")!;
        var m = t.GetMethod("BuildTokenLookup",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(IEnumerable<TaskInfo>), typeof(ITokenAggregator) })!;
        return (Dictionary<string, TaskTokenSummary>)m.Invoke(null, new object[] { jobs, tokens })!;
    }
}
