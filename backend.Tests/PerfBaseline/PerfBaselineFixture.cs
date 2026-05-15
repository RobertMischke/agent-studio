using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Tests.PerfBaseline;

/// <summary>
/// Shared fixture that materializes a synthetic workspace under tempdir and
/// builds the full runtime graph the polled endpoints traverse. Mirrors the
/// builder in JobsEndpointPerfTests so the two stay calibrated against the
/// same service shape.
/// </summary>
internal sealed class PerfBaselineFixture : IDisposable
{
    public string WatchPath { get; }
    public string ProjectName { get; }
    public IConfiguration Config { get; }
    public JobScannerService Scanner { get; }
    public TaskRunnerService Runners { get; }
    public CliRouter Router { get; }
    public SummaryGenerationService Summary { get; }
    public JobStateMachine States { get; }
    public ProjectTokenUsageService TokenUsage { get; }

    public JobIndexCache? IndexCache { get; }

    public PerfBaselineFixture(int jobCount, string scenarioTag = "perf", bool withCache = false)
    {
        ProjectName = $"perf-{scenarioTag}-{jobCount}";
        WatchPath = Path.Combine(Path.GetTempPath(), $"atp-{scenarioTag}-{Guid.NewGuid():N}");
        foreach (var state in JobStates.All)
            Directory.CreateDirectory(Path.Combine(WatchPath, state));

        // Distribute jobs across realistic lanes: bulk in archive, a few
        // ready, a couple in progress, some completed.
        var laneMix = new[]
        {
            (JobStates.Archive,  0.80),
            (JobStates.Completed, 0.10),
            (JobStates.Ready,     0.05),
            (JobStates.AutoReview, 0.03),
            (JobStates.HumanReview,0.015),
            (JobStates.Progress,   0.005),
        };
        var written = 0;
        foreach (var (lane, share) in laneMix)
        {
            var count = (int)Math.Round(jobCount * share);
            for (var i = 0; i < count && written < jobCount; i++, written++)
            {
                WriteJob(lane, $"job-{written:D5}", $"Job {written}");
            }
        }
        // Pad out any remainder into archive.
        while (written < jobCount)
        {
            WriteJob(JobStates.Archive, $"job-{written:D5}", $"Job {written}");
            written++;
        }

        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = WatchPath
            })
            .Build();

        Summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, Config);
        Scanner = new JobScannerService(Config, NullLogger<JobScannerService>.Instance, Summary);
        if (withCache)
        {
            IndexCache = new JobIndexCache(Scanner, NullLogger<JobIndexCache>.Instance, Config);
            Scanner.SetIndexCache(IndexCache);
        }
        States = new JobStateMachine(Scanner, NullLogger<JobStateMachine>.Instance);
        var sessions = new JobSessionLog(Scanner, NullLogger<JobSessionLog>.Instance);
        var mutations = new JobMutationService(Scanner, NullLogger<JobMutationService>.Instance);

        var cliEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var copilot = new CopilotCliService(
            NullLogger<CopilotCliService>.Instance, Config,
            new CopilotModelDiscovery(NullLogger<CopilotModelDiscovery>.Instance, cliEnv, Config),
            cliEnv);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, Config);
        var claude = new ClaudeCliService(NullLogger<ClaudeCliService>.Instance, Config);
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, Config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, Config);
        Router = new CliRouter(copilot, claude, codex, gemini);

        var contextUsageParser = new ContextUsageParser();
        var prompts = new RuntimePromptService(Config, NullLogger<RuntimePromptService>.Instance);
        var projectSettings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, Config);
        var git = new GitService(NullLogger<GitService>.Instance, Scanner, Config, prompts);
        var transitions = new JobTransitionService(Scanner, States, mutations, git, projectSettings, NullLogger<JobTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);
        var globalStore = new GlobalOrchestratorSessionStore(Config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var globalBoot = new GlobalOrchestratorBootstrap(NullLogger<GlobalOrchestratorBootstrap>.Instance, globalStore, orchestratorRunner, Scanner, Config);

        var quotaCacheStore = new QuotaCacheStore(Config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), Config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, Config);
        var pickupFailures = new PickupFailureLog(Config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(Config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(Config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);
        var indexCache = new JobIndexCache(Scanner, NullLogger<JobIndexCache>.Instance, Config);
        Scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            Scanner, mutations, States, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        Runners = new TaskRunnerService(
            Config, NullLogger<TaskRunnerService>.Instance, Scanner, States, mutations, sessions,
            copilot, Router, contextUsageParser, Summary, prompts, transitions, projectSettings,
            quotaService, quotaCaps,
            chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, globalBoot, git, pickupFailures, infraBreaker, taskAccess);

        TokenUsage = new ProjectTokenUsageService(orchestratorLog, Scanner);
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(WatchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            id = slug,
            title,
            state,
            order = 1,
            agent = "claude",
            cliType = "claude"
        });
        File.WriteAllText(Path.Combine(dir, "job.json"), json);
    }

    public void Dispose()
    {
        try { Directory.Delete(WatchPath, recursive: true); } catch { /* best-effort */ }
    }
}
