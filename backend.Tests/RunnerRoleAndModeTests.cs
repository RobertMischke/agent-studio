using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0044 coverage:
///   - <see cref="RunnerRoles.ResolveFromConfig"/> derives test-subject from
///     <c>Environment:IsDev=true</c> when no explicit <c>Runner:Role</c> is set,
///     and the explicit value always wins.
///   - <see cref="ProjectRunner.TickAsync"/> skips the pickup branch when the
///     runner's role is <see cref="RunnerRole.TestSubject"/>, even when mode is
///     <c>auto-continuous</c> and the ready queue is non-empty.
///   - <see cref="ProjectRunner.RequestModeChange"/> defers a manual / paused
///     request while a job is active and applies it on the next active-job
///     clear. The <see cref="ModeChangeResult"/> carries the deferred shape so
///     the endpoint can surface <c>PendingMode</c> + <c>WillApplyAfterJobId</c>.
///   - <see cref="PickupLockFile"/> rejects a foreign-held lock and reclaims a
///     stale one whose pid is no longer running.
/// </summary>
public sealed class RunnerRoleAndModeTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public RunnerRoleAndModeTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-runner-role-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveFromConfig_NoExplicitRole_IsDevTrue_DerivesTestSubject()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment:IsDev"] = "true" })
            .Build();

        Assert.Equal(RunnerRole.TestSubject, RunnerRoles.ResolveFromConfig(cfg));
    }

    [Fact]
    public void ResolveFromConfig_NoExplicitRole_IsDevFalse_DerivesOrchestrator()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment:IsDev"] = "false" })
            .Build();

        Assert.Equal(RunnerRole.Orchestrator, RunnerRoles.ResolveFromConfig(cfg));
    }

    [Fact]
    public void ResolveFromConfig_ExplicitRoleWinsOverIsDev()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runner:Role"] = "orchestrator",
                ["Environment:IsDev"] = "true"
            })
            .Build();

        Assert.Equal(RunnerRole.Orchestrator, RunnerRoles.ResolveFromConfig(cfg));
    }

    [Fact]
    public async Task TestSubjectRunner_AutoContinuous_ReadyJob_NeverPicks()
    {
        // A ready job is sitting in 2-ready; the runner is auto-continuous; a
        // foreground orchestrator would pick it on the next tick. The
        // test-subject role must short-circuit before the queue probe so the
        // job stays put.
        SeedReadyJob("ready-1");
        var runner = BuildRunner(role: RunnerRole.TestSubject);
        runner.SetMode("auto-continuous", "test setup");

        await runner.TickAsync(CancellationToken.None);

        Assert.Null(runner.GetStatus().ActiveJobId);
        // And the ready job is still there.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Ready, "ready-1")));
    }

    [Fact]
    public void RequestModeChange_NoActiveJob_AppliesImmediately()
    {
        var runner = BuildRunner(role: RunnerRole.Orchestrator);
        runner.SetMode("auto-continuous", "test setup");

        var result = runner.RequestModeChange("manual", "operator click");

        Assert.NotNull(result);
        Assert.Equal(ModeChangeOutcome.Applied, result.Outcome);
        Assert.Equal("manual", result.CurrentMode);
        Assert.Null(result.PendingMode);
        Assert.Null(result.WillApplyAfterJobId);
        Assert.Equal("manual", runner.GetStatus().Mode);
    }

    [Fact]
    public void RequestModeChange_ActiveJob_ManualRequest_DefersAndKeepsLiveMode()
    {
        var runner = BuildRunner(role: RunnerRole.Orchestrator);
        runner.SetMode("auto-continuous", "test setup");
        runner.SetActiveJobForTest("running-job-x");

        var result = runner.RequestModeChange("manual", "operator click");

        Assert.NotNull(result);
        Assert.Equal(ModeChangeOutcome.Deferred, result.Outcome);
        Assert.Equal("auto-continuous", result.CurrentMode);
        Assert.Equal("manual", result.PendingMode);
        Assert.Equal("running-job-x", result.WillApplyAfterJobId);

        var status = runner.GetStatus();
        Assert.Equal("auto-continuous", status.Mode);
        Assert.Equal("manual", status.PendingMode);
        Assert.Equal("running-job-x", status.PendingModeWillApplyAfter);
        Assert.True(runner.HasPendingMode);
    }

    [Fact]
    public void RequestModeChange_ActiveJob_AutoRequest_AppliesImmediately()
    {
        // Switching auto-single -> auto-continuous while a job runs should
        // apply now: the new mode is still an auto-* value, so there is no
        // "stop after this finishes" intent to defer.
        var runner = BuildRunner(role: RunnerRole.Orchestrator);
        runner.SetMode("auto-single", "test setup");
        runner.SetActiveJobForTest("running-job-y");

        var result = runner.RequestModeChange("auto-continuous", "operator click");

        Assert.NotNull(result);
        Assert.Equal(ModeChangeOutcome.Applied, result.Outcome);
        Assert.Equal("auto-continuous", result.CurrentMode);
        Assert.False(runner.HasPendingMode);
    }

    [Fact]
    public void RequestModeChange_ActiveJobClears_DeferredModeApplies()
    {
        var runner = BuildRunner(role: RunnerRole.Orchestrator);
        runner.SetMode("auto-continuous", "test setup");
        runner.SetActiveJobForTest("running-job-z");

        runner.RequestModeChange("manual", "operator click");
        Assert.True(runner.HasPendingMode);

        // Simulate the active job clearing (the finally block in
        // OnCliFinishedAsync / ClearActiveJobIfMatches calls ApplyPendingModeIfAny
        // right after _activeJobId nulls out).
        runner.ClearActiveJobIfMatches("running-job-z", reason: "test clear");

        Assert.False(runner.HasPendingMode);
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.Null(runner.GetStatus().PendingMode);
        Assert.Null(runner.GetStatus().PendingModeWillApplyAfter);
    }

    [Fact]
    public void PickupLockFile_ForeignAliveOwner_RejectsAcquire()
    {
        var folder = Path.Combine(_watchPath, JobStates.Progress, "lock-test");
        Directory.CreateDirectory(folder);
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);

        // Stamp the lock with a "foreign" pid that is alive (use the current
        // test process id but a different backend name so the IsSameOwner
        // check returns false).
        var foreignOwner = new PickupLockOwner
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            ProjectName = ProjectName
        };
        var firstOutcome = lockFile.TryAcquire(folder, foreignOwner, out var _firstExisting);
        Assert.Equal(LockAcquireOutcome.Acquired, firstOutcome);

        // Second acquire from a different backend (dev) should see a foreign
        // live owner and refuse.
        var devOwner = new PickupLockOwner
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.TestSubject,
            BackendName = "dev",
            ProjectName = ProjectName
        };
        var secondOutcome = lockFile.TryAcquire(folder, devOwner, out var existing);
        Assert.Equal(LockAcquireOutcome.ForeignHeld, secondOutcome);
        Assert.NotNull(existing);
        Assert.Equal("stable", existing!.BackendName);
    }

    [Fact]
    public void PickupLockFile_SameOwner_ReturnsAlreadyOwn()
    {
        var folder = Path.Combine(_watchPath, JobStates.Progress, "lock-reentrant");
        Directory.CreateDirectory(folder);
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);

        var owner = new PickupLockOwner
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            ProjectName = ProjectName
        };

        Assert.Equal(LockAcquireOutcome.Acquired, lockFile.TryAcquire(folder, owner, out _));
        Assert.Equal(LockAcquireOutcome.AlreadyOwn, lockFile.TryAcquire(folder, owner, out _));
    }

    [Fact]
    public void PickupLockFile_StalePid_ReclaimsLock()
    {
        var folder = Path.Combine(_watchPath, JobStates.Progress, "lock-stale");
        Directory.CreateDirectory(folder);
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);

        // Plant a lock owned by a definitely-dead pid (negative numbers are not
        // valid process ids; the helper short-circuits to "not alive" on pid<=0).
        var deadOwner = new PickupLockOwner
        {
            Pid = -1,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "ghost-backend",
            ProjectName = ProjectName
        };
        // First acquire seeds the file.
        Assert.Equal(LockAcquireOutcome.Acquired, lockFile.TryAcquire(folder, deadOwner, out _));

        var fresh = new PickupLockOwner
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            ProjectName = ProjectName
        };
        Assert.Equal(LockAcquireOutcome.Stale, lockFile.TryAcquire(folder, fresh, out var prior));
        Assert.NotNull(prior);
        Assert.Equal("ghost-backend", prior!.BackendName);
    }

    [Fact]
    public void PickupLockFile_Release_OnlyDeletesWhenOwner()
    {
        var folder = Path.Combine(_watchPath, JobStates.Progress, "lock-release");
        Directory.CreateDirectory(folder);
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);

        var stable = new PickupLockOwner
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            ProjectName = ProjectName
        };
        lockFile.TryAcquire(folder, stable, out _);

        // A different backend (dev) tries to release; the file must stay.
        var dev = stable with { BackendName = "dev", Role = RunnerRoles.TestSubject };
        lockFile.Release(folder, dev);
        Assert.True(File.Exists(Path.Combine(folder, PickupLockFile.LockFileName)));

        // The real owner releases; the file goes away.
        lockFile.Release(folder, stable);
        Assert.False(File.Exists(Path.Combine(folder, PickupLockFile.LockFileName)));
    }

    private void SeedReadyJob(string slug)
    {
        var folder = Path.Combine(_watchPath, JobStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "task body");
        File.WriteAllText(Path.Combine(folder, "job.json"),
            $$"""
            {
              "id": "{{slug}}",
              "title": "ready test job",
              "state": "2-ready",
              "order": 1,
              "agent": "claude",
              "cliType": "claude",
              "model": "claude-opus-4-7"
            }
            """);
    }

    private ProjectRunner BuildRunner(RunnerRole role = RunnerRole.Orchestrator)
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
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            bus: null,
            role: role,
            pickupLock: null,
            pickupLockOwner: null);
    }
}
