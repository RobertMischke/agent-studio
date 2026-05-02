using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Endpoints.Jobs;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Runner;
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

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

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
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, config, codexDiscovery);
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

        var runners = new TaskRunnerService(
            config, NullLogger<TaskRunnerService>.Instance, scanner, states, mutations, sessions,
            copilot, router, contextUsageParser, summary, prompts, transitions, projectSettings,
            chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot);
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
}
