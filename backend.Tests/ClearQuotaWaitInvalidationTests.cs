using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2699: production logs showed ScanAllJobsRaw running every ~14s
/// (3513 times in 14h) instead of the ~30s safety TTL. A major source was
/// <c>ProjectRunner.ClearQuotaWait</c> calling <c>InvalidateCache()</c>
/// unconditionally from seven call sites in the 5s runner tick, even for
/// candidates that never had a <c>quota-wait.json</c> marker to begin with.
/// Locks: no marker present -> the tick-path guard skips the invalidation;
/// a real marker -> the file is deleted AND the invalidation still fires,
/// preserving the read-after-write contract documented at
/// <c>ProjectRunner.cs:824-826</c> (a recovered provider wait must not be
/// re-read from a stale task snapshot on the next tick).
/// </summary>
public sealed class ClearQuotaWaitInvalidationTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly string _repoRoot;

    public ClearQuotaWaitInvalidationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-clear-quota-wait-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        _repoRoot = Path.Combine(_workspaceRoot, "repos", ProjectName);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NoMarkerPresent_DoesNotInvalidateCache()
    {
        var (runner, cache) = BuildRunner();
        var info = MakeJob("job-a");

        var before = cache.MutationInvalidations;
        InvokeClearQuotaWait(runner, info);

        Assert.Equal(before, cache.MutationInvalidations);
    }

    [Fact]
    public void MarkerPresent_DeletesFileAndStillInvalidatesCache()
    {
        var (runner, cache) = BuildRunner();
        var info = MakeJob("job-b");
        QuotaWaitMarker.Write(info.FolderPath, new QuotaWaitRecord
        {
            CliType = "claude",
            ResetAt = DateTime.UtcNow.AddHours(1),
            ThresholdMinutes = 30,
            Reason = "claude: limited",
        });
        Assert.True(File.Exists(Path.Combine(info.FolderPath, QuotaWaitMarker.FileName)));

        var before = cache.MutationInvalidations;
        InvokeClearQuotaWait(runner, info);

        Assert.False(File.Exists(Path.Combine(info.FolderPath, QuotaWaitMarker.FileName)));
        Assert.Equal(before + 1, cache.MutationInvalidations);
    }

    private static void InvokeClearQuotaWait(ProjectRunner runner, TaskInfo info)
    {
        var method = typeof(ProjectRunner).GetMethod("ClearQuotaWait", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ProjectRunner), "ClearQuotaWait");
        method.Invoke(runner, new object[] { info });
    }

    private TaskInfo MakeJob(string id)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Ready, id);
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = id,
            Title = id,
            State = TaskStates.Ready,
            FolderPath = folder,
            WatchPath = _watchPath,
            ProjectName = ProjectName,
            CliType = "claude",
        };
    }

    private (ProjectRunner Runner, TaskIndexCache Cache) BuildRunner()
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
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);

        var claude = GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = GenericCliExecutionService.ForCodex(NullLogger<GenericCliExecutionService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = GenericCliExecutionService.ForAntigravity(NullLogger<GenericCliExecutionService>.Instance, config);
        var router = new CliRouter(claude, codex, gemini);

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
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess,
            timeline: timeline);

        return (runner, indexCache);
    }
}
