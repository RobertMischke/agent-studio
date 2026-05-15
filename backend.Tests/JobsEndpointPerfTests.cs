using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Endpoints.Jobs;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression test for the multi-second lag the user hit on /api/jobs and
/// /api/jobs/grouped after the auto-loop snapshot was folded onto every
/// JobInfo.
///
/// <para>
/// Root cause that this test pins down: <c>WithRuntime</c> looked up the
/// auto-loop state via <c>TaskRunnerService.GetStuckLoopStateForJob(jobId,
/// watchPath)</c>, which called <c>JobScannerService.FindJob</c>, which
/// performed a full <c>ScanAllJobs</c> (disk walk + JSON parse) on every
/// invocation. With ~150 jobs that meant the grouped endpoint did 150
/// full disk rescans per HTTP call, taking 7-15 seconds. The frontend
/// polls grouped jobs every 5 seconds, so the UI was permanently
/// blocked behind the previous poll.
/// </para>
///
/// <para>
/// The contract being locked: enriching N JobInfos with WithRuntime must
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
        foreach (var state in JobStates.All)
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
            WriteJob(JobStates.Archive, $"job-{i:D4}");
        }

        var (router, runners) = BuildRuntime(projectName);
        var scanner = BuildScanner();

        // Warm one scan so the JIT / file system cache are settled — we are
        // measuring the overlay, not the first-touch cost.
        var jobs = scanner.ScanAllJobs();
        Assert.Equal(jobCount, jobs.Count);
        _ = jobs.Select(j => JobEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();

        // Act — measure the overlay only. Even if ScanAllJobs gets faster
        // later, the regression we are guarding against was inside the
        // overlay (per-job FindJob causing a full rescan).
        var sw = Stopwatch.StartNew();
        var enriched = jobs.Select(j => JobEndpointHelpersAccessor.WithRuntime(j, router, runners)).ToList();
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
            "any other helper that might be calling JobScannerService.FindJob inside the " +
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
        var tokens = new FakeTokenAggregator(new Dictionary<string, Dictionary<string, JobTokenSummary>>(StringComparer.OrdinalIgnoreCase)
        {
            ["project-a"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new JobTokenSummary { TotalTokens = 10 },
                ["job-b"] = new JobTokenSummary { TotalTokens = 20 },
            },
            ["project-b"] = new(StringComparer.Ordinal)
            {
                ["job-a"] = new JobTokenSummary { TotalTokens = 30 },
            },
        });

        var lookup = JobEndpointHelpersAccessor.BuildTokenLookup(jobs, tokens);

        Assert.Equal(2, tokens.Calls.Count);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-a" && c.WatchPath == jobs[0].WatchPath);
        Assert.Contains(tokens.Calls, c => c.ProjectName == "project-b" && c.WatchPath == jobs[2].WatchPath);
        Assert.Equal(10, lookup[jobs[0].JobKey].TotalTokens);
        Assert.Equal(20, lookup[jobs[1].JobKey].TotalTokens);
        Assert.Equal(30, lookup[jobs[2].JobKey].TotalTokens);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static JobInfo MakeJob(string id, string projectName, string watchPath) => new()
    {
        Id = id,
        JobKey = $"{watchPath}::{id}",
        Title = id,
        State = JobStates.Progress,
        ProjectName = projectName,
        WatchPath = watchPath,
        FolderPath = Path.Combine(watchPath, JobStates.Progress, id),
    };

    private JobScannerService BuildScanner()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
    }

    private (CliRouter router, TaskRunnerService runners) BuildRuntime(string projectName)
    {
        var config = BuildConfig(projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);

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
        var transitions = new JobTransitionService(scanner, states, mutations, git, projectSettings, NullLogger<JobTransitionService>.Instance);
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
        var indexCache = new JobIndexCache(scanner, NullLogger<JobIndexCache>.Instance, config);
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
    private readonly IReadOnlyDictionary<string, Dictionary<string, JobTokenSummary>> _perProject;
    public List<(string ProjectName, string WatchPath)> Calls { get; } = [];

    public FakeTokenAggregator(IReadOnlyDictionary<string, Dictionary<string, JobTokenSummary>> perProject)
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

    public Dictionary<string, JobTokenSummary> WorkspacePerJob(string projectName, string watchPath)
    {
        Calls.Add((projectName, watchPath));
        return _perProject.TryGetValue(projectName, out var perJob)
            ? perJob
            : new Dictionary<string, JobTokenSummary>(StringComparer.Ordinal);
    }
}

/// <summary>
/// JobEndpointHelpers.WithRuntime is internal; this thin accessor lets the
/// regression test reach it without making the helper public on its own.
/// Lives in the test project so the production surface stays unchanged.
/// </summary>
internal static class JobEndpointHelpersAccessor
{
    public static JobInfo WithRuntime(JobInfo job, CliRouter router, TaskRunnerService runners)
    {
        // Reflection over the internal helper. Keeps the production access
        // modifier honest while still letting the test call into it.
        var t = typeof(OrchestratorApi.Endpoints.Jobs.JobCrudEndpoints).Assembly
            .GetType("OrchestratorApi.Endpoints.Jobs.JobEndpointHelpers")!;
        var m = t.GetMethod("WithRuntime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(JobInfo), typeof(CliRouter), typeof(TaskRunnerService) })!;
        return (JobInfo)m.Invoke(null, new object[] { job, router, runners })!;
    }

    public static Dictionary<string, JobTokenSummary> BuildTokenLookup(IEnumerable<JobInfo> jobs, ITokenAggregator tokens)
    {
        var t = typeof(OrchestratorApi.Endpoints.Jobs.JobCrudEndpoints).Assembly
            .GetType("OrchestratorApi.Endpoints.Jobs.JobEndpointHelpers")!;
        var m = t.GetMethod("BuildTokenLookup",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            new[] { typeof(IEnumerable<JobInfo>), typeof(ITokenAggregator) })!;
        return (Dictionary<string, JobTokenSummary>)m.Invoke(null, new object[] { jobs, tokens })!;
    }
}
