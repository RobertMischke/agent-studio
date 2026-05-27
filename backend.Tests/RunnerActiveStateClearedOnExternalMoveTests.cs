using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract added for the wedge bug where moving the active job
/// out of <c>3-progress</c> via <c>POST /api/jobs/{id}/move</c> left
/// <see cref="ProjectRunner"/>'s in-memory <c>_activeJobId</c> pinned at
/// the slug. Every subsequent pickup tick saw <c>active != null</c> and
/// short-circuited; the project was wedged until a backend restart.
///
/// <list type="number">
///   <item><see cref="JobTransitionService.MoveAsync"/> raises
///   <c>OnJobMoved</c> with the resolved project, job id, source state,
///   and target state. Wired in <c>Program.cs</c> to call
///   <see cref="TaskRunnerService.ClearActiveJobForProject"/>, which
///   delegates to <see cref="ProjectRunner.ClearActiveJobIfMatches"/>.</item>
///   <item><see cref="ProjectRunner.ClearActiveJobIfMatches"/> releases
///   the in-memory latch, idempotent on repeat calls.</item>
///   <item><see cref="ProjectRunner.ReconcileActiveJobAgainstDisk"/>
///   detects an external folder move (no API event ever fired) and
///   clears the latch so the next pickup tick is unblocked.</item>
/// </list>
/// </summary>
public sealed class RunnerActiveStateClearedOnExternalMoveTests : IDisposable
{
    private readonly string _watchPath;
    private const string ProjectName = "demo";

    public RunnerActiveStateClearedOnExternalMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-runner-clear-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task MoveAsync_FromProgressToReady_FiresOnJobMovedWithResolvedStates()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var captured = new List<(string Project, string JobId, string From, string To)>();
        deps.Transitions.OnJobMoved += (project, jobId, from, to) =>
            captured.Add((project, jobId, from, to));

        var outcome = await deps.Transitions.MoveAsync("demo-task", JobStates.Ready, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var evt = Assert.Single(captured);
        Assert.Equal(ProjectName, evt.Project);
        Assert.Equal("demo-task", evt.JobId);
        Assert.Equal(JobStates.Progress, evt.From);
        Assert.Equal(JobStates.Ready, evt.To);
    }

    [Fact]
    public async Task MoveAsync_FromProgressToArchive_FiresOnJobMoved()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var captured = new List<(string Project, string JobId, string From, string To)>();
        deps.Transitions.OnJobMoved += (project, jobId, from, to) =>
            captured.Add((project, jobId, from, to));

        var outcome = await deps.Transitions.MoveAsync("demo-task", JobStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var evt = Assert.Single(captured);
        Assert.Equal(JobStates.Progress, evt.From);
        Assert.Equal(JobStates.Archive, evt.To);
    }

    [Fact]
    public async Task MoveAsync_NoStateChange_DoesNotFireEvent()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var fired = 0;
        deps.Transitions.OnJobMoved += (_, _, _, _) => fired++;

        var outcome = await deps.Transitions.MoveAsync("demo-task", JobStates.Progress, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ClearActiveJobIfMatches_OnMatchingId_ClearsLatchAndIsIdempotent()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        var first = runner.ClearActiveJobIfMatches("demo-task", "test-clear");
        var second = runner.ClearActiveJobIfMatches("demo-task", "second-call");

        Assert.True(first);
        Assert.False(second);
        Assert.Null(runner.GetStatus().ActiveJobId);
    }

    [Fact]
    public void ClearActiveJobIfMatches_OnNonMatchingId_NoOp()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        var cleared = runner.ClearActiveJobIfMatches("other-task", "test-clear");

        Assert.False(cleared);
        Assert.Equal("demo-task", runner.GetStatus().ActiveJobId);
    }

    [Fact]
    public void Reconcile_ActiveJobMovedToReady_ClearsLatch()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        // Simulate an external (non-API) move: rename the folder out of
        // 3-progress without going through JobTransitionService. This is the
        // exact code path the boot-time stuck-folder sweep and any hand
        // edit take, neither of which would fire OnJobMoved.
        Directory.Move(
            Path.Combine(_watchPath, JobStates.Progress, "demo-task"),
            Path.Combine(_watchPath, JobStates.Ready, "demo-task"));

        // First reconcile call must clear the latch; a second is a no-op.
        var firstCleared = runner.ReconcileActiveJobAgainstDisk();
        var secondCleared = runner.ReconcileActiveJobAgainstDisk();

        Assert.True(firstCleared);
        Assert.False(secondCleared);
        Assert.Null(runner.GetStatus().ActiveJobId);
    }

    [Fact]
    public void Reconcile_ActiveJobFolderDeleted_ClearsLatch()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        Directory.Delete(Path.Combine(_watchPath, JobStates.Progress, "demo-task"), recursive: true);

        var cleared = runner.ReconcileActiveJobAgainstDisk();

        Assert.True(cleared);
        Assert.Null(runner.GetStatus().ActiveJobId);
    }

    [Fact]
    public void Reconcile_ActiveJobStillInProgress_NoOp()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        var cleared = runner.ReconcileActiveJobAgainstDisk();

        Assert.False(cleared);
        Assert.Equal("demo-task", runner.GetStatus().ActiveJobId);
    }

    [Fact]
    public async Task MoveAsync_PlusEventHandler_ClearsActiveJobInRunner()
    {
        // End-to-end of the wired path (mirrors Program.cs): a successful
        // move out of 3-progress fires OnJobMoved, which the handler turns
        // into runner.ClearActiveJobIfMatches. After this, the runner
        // reports active=null and the next pickup tick is unblocked.
        WriteJob(JobStates.Progress, "demo-task");
        var deps = BuildDeps();
        var runner = BuildRunner(deps);
        runner.SetActiveJobForTest("demo-task");

        deps.Transitions.OnJobMoved += (project, jobId, from, _) =>
        {
            if (from != JobStates.Progress) return;
            if (project != ProjectName) return;
            runner.ClearActiveJobIfMatches(jobId, "job moved out of 3-progress externally");
        };

        var outcome = await deps.Transitions.MoveAsync("demo-task", JobStates.Ready, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Null(runner.GetStatus().ActiveJobId);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"cliType\":\"copilot\"}}");
    }

    private sealed record Deps(
        IConfiguration Config,
        JobScannerService Scanner,
        JobStateMachine States,
        JobMutationService Mutations,
        JobSessionLog Sessions,
        SummaryGenerationService Summary,
        RuntimePromptService Prompts,
        ProjectSettingsService Settings,
        GitService Git,
        JobTransitionService Transitions,
        OrchestratorChatLog ChatLog,
        OrchestratorLog OrchestratorLog,
        OrchestratorRunner OrchestratorRunner,
        OrchestratorSessionStore OrchestratorSessions,
        CliRouter Router,
        OrchestratorApi.Services.TaskAccess.ITaskAccess TaskAccess);

    private Deps BuildDeps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _watchPath
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), NullLogger<JobMutationService>.Instance);
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
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, config);
        var router = new CliRouter(copilot, claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        return new Deps(config, scanner, states, mutations, sessions, summary, prompts, settings, git,
            transitions, chatLog, orchestratorLog, orchestratorRunner, orchestratorSessions, router, taskAccess);
    }

    private ProjectRunner BuildRunner(Deps d)
    {
        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var quotaCacheStore = new QuotaCacheStore(d.Config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), d.Config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, d.Config);
        var pickupFailures = new PickupFailureLog(d.Config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(d.Config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(d.Config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            d.Scanner, d.States, d.Sessions, d.Router,
            d.Summary, d.Prompts, d.Transitions, d.ChatLog, d.Mutations,
            d.OrchestratorLog, d.OrchestratorRunner, d.OrchestratorSessions,
            d.Settings, quotaService, quotaCaps, d.Git, pickupFailures, infraBreaker, d.TaskAccess, bus: null);
    }
}
