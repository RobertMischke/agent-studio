using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
using System.Reflection;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0052 Slice 2: the runner must read each project's
/// <c>MaxParallelism</c> and surface the slot picture on its status so the
/// FE Overview/Timeline can render occupancy + the pick decision without
/// re-deriving it. This is the additive, zero-control-flow-change layer:
/// at <c>MaxParallelism == 1</c> the runner behaves byte-for-byte as before
/// (single sequential slot), so we only assert the *surface* here. The
/// concurrent N-slot execution rewrite is the documented supervised-landing
/// remainder; the pure pick-gate it will call is covered by
/// <c>ParallelSlotPolicyTests</c>.
/// </summary>
public sealed class RunnerSlotWiringTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public RunnerSlotWiringTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-runner-slot-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        _repoRoot = Path.Combine(_workspaceRoot, "repos", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void GetStatus_DefaultsToSingleIdleSlot()
    {
        var (runner, _) = BuildRunner();

        var status = runner.GetStatus();

        Assert.Equal(1, status.MaxParallelism);
        Assert.Equal(0, status.OccupiedSlots);
        Assert.Null(status.LastPickReason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void GetStatus_ReflectsConfiguredMaxParallelism(int configured)
    {
        var (runner, settings) = BuildRunner();
        settings.SetMaxParallelism(ProjectName, configured);

        var status = runner.GetStatus();

        Assert.Equal(configured, status.MaxParallelism);
        Assert.Equal(0, status.OccupiedSlots);
    }

    [Fact]
    public void RenderPrompt_ForWorktreeRun_RewritesMainCheckoutPathsAndAddsContainmentNotice()
    {
        var (runner, _) = BuildRunner();
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "fix-worktree-paths");
        Directory.CreateDirectory(jobFolder);
        var promptPath = Path.Combine(jobFolder, "prompt.md");
        File.WriteAllText(promptPath,
            $"Edit {_repoRoot}\\backend\\Services\\Tasks\\TaskStateMachine.cs and run git -C {_repoRoot} status.");
        var worktree = Path.Combine(_workspaceRoot, "worktrees", "fix-worktree-paths");
        Directory.CreateDirectory(worktree);
        var info = new TaskInfo
        {
            Id = "fix-worktree-paths",
            Title = "Fix worktree paths",
            State = TaskStates.Progress,
            FolderPath = jobFolder,
            WatchPath = _watchPath,
            ProjectName = ProjectName
        };
        var plan = new RunPlan(
            PromptTemplate: RuntimePromptService.RunnerFreshStart,
            PromptVariables: new Dictionary<string, string?>
            {
                ["prompt_path"] = promptPath,
                ["job_folder"] = jobFolder,
                ["user_followup"] = null
            },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "start",
            EventReason: null,
            EventInputSessionId: null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

        var rendered = InvokeRenderPrompt(runner, plan, info, worktree);

        Assert.Contains("## Worktree containment", rendered);
        Assert.Contains(worktree, rendered);
        Assert.DoesNotContain($"git -C {_repoRoot} status", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Edit {_repoRoot}\\backend", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"git -C {worktree} status", rendered);
        Assert.Contains($"Working directory: `{worktree}`", rendered);
        Assert.Contains($"Git repository for status/diff/commits: `{worktree}`", rendered);
    }

    [Fact]
    public void RenderPrompt_ForWorktreePromptOverride_StillAddsContainmentNotice()
    {
        var (runner, _) = BuildRunner();
        var jobFolder = Path.Combine(_watchPath, TaskStates.Progress, "fix-worktree-override-paths");
        Directory.CreateDirectory(jobFolder);
        var worktree = Path.Combine(_workspaceRoot, "worktrees", "fix-worktree-override-paths");
        Directory.CreateDirectory(worktree);
        var info = new TaskInfo
        {
            Id = "fix-worktree-override-paths",
            Title = "Fix worktree override paths",
            State = TaskStates.Progress,
            FolderPath = jobFolder,
            WatchPath = _watchPath,
            ProjectName = ProjectName
        };
        var plan = new RunPlan(
            PromptTemplate: null,
            PromptVariables: new Dictionary<string, string?>(),
            PromptOverride: $"Run tests in {_repoRoot} and inspect {_repoRoot}\\backend.",
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "start",
            EventReason: null,
            EventInputSessionId: null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

        var rendered = InvokeRenderPrompt(runner, plan, info, worktree);

        Assert.Contains("## Worktree containment", rendered);
        Assert.Contains(worktree, rendered);
        Assert.DoesNotContain($"Run tests in {_repoRoot}", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"inspect {_repoRoot}\\backend", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private (ProjectRunner Runner, ProjectSettingsService Settings) BuildRunner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _repoRoot,
            RepositoryPath = _repoRoot
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
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

        var runner = new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null);

        return (runner, settings);
    }

    private static string InvokeRenderPrompt(ProjectRunner runner, RunPlan plan, TaskInfo info, string runWorkingDir)
    {
        var method = typeof(ProjectRunner).GetMethod("RenderPrompt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ProjectRunner), "RenderPrompt");
        return (string)method.Invoke(runner, new object[] { plan, info, runWorkingDir })!;
    }
}
