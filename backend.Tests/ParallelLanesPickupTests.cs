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
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the parallel-lane invariant that drove the
/// <c>parallel-review-preparation-progress-pickup</c> task: the runner's
/// per-project pickup tick consumes only <c>3-progress</c> and
/// <c>2-ready</c>. Jobs sitting in the preparation lanes
/// (<c>1-preparation</c>, <c>1a-orchestrator-prep</c>,
/// <c>1b-needs-human-review</c>) or the review lanes
/// (<c>4-auto-review</c>, <c>5-human-review</c>) are processed by their
/// own background services and do NOT block pickup. ADR-0001's
/// single-coding-CLI-per-project rule is enforced separately by the
/// active-job latch inside <see cref="ProjectRunner.TickAsync"/>; the
/// pickup selector itself is lane-decoupled.
///
/// <para>
/// User framing on the source task: "Review und Preparation koennen
/// parallel passieren. Es gibt keinen Grund, dass In Progress nicht
/// nachgezogen wird." If this test ever starts failing because someone
/// added a lane-aware gate to <see cref="ProjectRunner.GetNextReadyJob"/>
/// or to the strict-iteration progress picker, that change is a
/// regression against the explicit user decision recorded in
/// ADR-0026's revised non-goals.
/// </para>
/// </summary>
public sealed class ParallelLanesPickupTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;

    public ParallelLanesPickupTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-parallel-lanes-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Jobs sitting in every non-progress / non-ready lane do not influence
    /// <see cref="ProjectRunner.GetNextReadyJob"/>. The selector still
    /// returns the oldest <c>2-ready</c> entry.
    /// </summary>
    [Fact]
    public void GetNextReadyJob_AllParallelLanesPopulated_StillReturnsReadyJob()
    {
        // Saturate every parallel lane.
        WriteJob(JobStates.Preparation, "prep-A");
        WriteJob(JobStates.OrchestratorPrep, "orch-prep-A");
        WriteJob(JobStates.NeedsHumanReview, "needs-human-A");
        WriteJob(JobStates.AutoReview, "auto-review-A");
        WriteJob(JobStates.HumanReview, "human-review-A");
        WriteJob(JobStates.Completed, "completed-A");

        // The one job that should be pickup-eligible.
        WriteJob(JobStates.Ready, "ready-eligible", order: 5);

        var runner = BuildRunner();

        var picked = runner.GetNextReadyJob();

        Assert.NotNull(picked);
        Assert.Equal("ready-eligible", picked!.Id);
        Assert.Equal(JobStates.Ready, picked.State);
    }

    /// <summary>
    /// When <c>2-ready</c> itself is empty, the selector returns
    /// <c>null</c> regardless of how many jobs are sitting in preparation
    /// or review lanes. This is the inverse half of the invariant: the
    /// parallel lanes are visible to their own services but never get
    /// promoted into a runner pickup by accident.
    /// </summary>
    [Fact]
    public void GetNextReadyJob_ReadyEmpty_PreparationAndReviewJobsAreIgnored()
    {
        WriteJob(JobStates.Preparation, "prep-A");
        WriteJob(JobStates.OrchestratorPrep, "orch-prep-A");
        WriteJob(JobStates.NeedsHumanReview, "needs-human-A");
        WriteJob(JobStates.AutoReview, "auto-review-A");
        WriteJob(JobStates.HumanReview, "human-review-A");

        var runner = BuildRunner();

        Assert.Null(runner.GetNextReadyJob());
    }

    /// <summary>
    /// Composite scenario that matches the user's note:
    /// <c>3-progress</c> is empty, parallel lanes have work in flight,
    /// and the runner must still pull <c>2-ready</c> forward. We exercise
    /// the same selector chain <see cref="ProjectRunner.TickAsync"/>
    /// runs: <c>TryPickProgressJobOrDeadLetter</c> first (returns
    /// <c>null</c>, since <c>3-progress</c> is empty) then
    /// <c>GetNextReadyJob</c> (returns the queued task).
    /// </summary>
    [Fact]
    public void Pipeline_ProgressEmpty_ReviewAndPreparationBusy_PullsReadyForward()
    {
        // Parallel lanes are busy (Review + Preparation).
        WriteJob(JobStates.Preparation, "prep-1");
        WriteJob(JobStates.OrchestratorPrep, "orch-prep-1");
        WriteJob(JobStates.AutoReview, "auto-review-1");
        WriteJob(JobStates.HumanReview, "human-review-1");

        // 3-progress is empty.
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, JobStates.Progress)));

        // 2-ready has the next task.
        WriteJob(JobStates.Ready, "next-task", order: 10);

        var runner = BuildRunner();

        var progressPick = InvokeProgressPicker(runner);
        Assert.Null(progressPick);

        var readyPick = runner.GetNextReadyJob();
        Assert.NotNull(readyPick);
        Assert.Equal("next-task", readyPick!.Id);
    }

    /// <summary>
    /// Progress-first priority is preserved when parallel lanes are also
    /// busy. ADR-0028 says any folder in <c>3-progress</c> wins over
    /// <c>2-ready</c>; that priority is independent of what sits in the
    /// review / preparation lanes.
    /// </summary>
    [Fact]
    public void Pipeline_ProgressJobExists_ReviewAndPreparationBusy_PicksProgressFirst()
    {
        // Parallel lanes busy.
        WriteJob(JobStates.Preparation, "prep-1");
        WriteJob(JobStates.AutoReview, "auto-review-1");
        WriteJob(JobStates.HumanReview, "human-review-1");

        // 3-progress has a resumable folder.
        WriteJob(JobStates.Progress, "resume-me");

        // 2-ready has a fresh task; it must NOT be chosen first.
        WriteJob(JobStates.Ready, "fresh-ready", order: 1);

        var runner = BuildRunner();

        var progressPick = InvokeProgressPicker(runner);
        Assert.NotNull(progressPick);
        Assert.Equal("resume-me", progressPick!.Id);
    }

    // ===== Helpers =====

    private static JobInfo? InvokeProgressPicker(ProjectRunner runner)
    {
        var method = typeof(ProjectRunner).GetMethod(
            "TryPickProgressJobOrDeadLetter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(runner, null) as JobInfo;
    }

    private void WriteJob(string state, string slug, int order = 1)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order}," +
            "\"agent\":\"copilot\",\"cliType\":\"copilot\",\"ownerClientId\":\"local-default\"}");
    }

    private ProjectRunner BuildRunner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new JobTransitionService(scanner, states, mutations, git, settings, NullLogger<JobTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new JobIndexCache(scanner, NullLogger<JobIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        var cliEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var copilot = new CopilotCliService(
            NullLogger<CopilotCliService>.Instance, config,
            new CopilotModelDiscovery(NullLogger<CopilotModelDiscovery>.Instance, cliEnv, config),
            cliEnv);
        var claude = new ClaudeCliService(NullLogger<ClaudeCliService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = new CodexCliService(
            NullLogger<CodexCliService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, config);
        var router = new CliRouter(copilot, claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null);
    }
}
