using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Run-Liveness Slice B end-to-end over a temp workspace: the steer-timeout
/// monitor turns a durable <c>steer-pending.json</c> marker into a bounded
/// resolution (auto-answer + resume, or blocked escalation), so a steered card
/// never hangs. Reconstructs the 2062/2067/2068 evidence scenario as a fixture
/// (concept Rule 2, "Beleg-Szenario der drei Karten als Fixture nachstellen").
/// </summary>
public sealed class SteerTimeoutMonitorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public SteerTimeoutMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-steer-timeout-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ThreeCardEvidence_ResolvesEveryTimedOutWait_NoneKeepsWaiting()
    {
        // 2067: answerable ("already implemented?") -> auto-answered + resumed.
        WriteSteerPending("card-2067", question: "Is the iframe embed already implemented in develop?", waitedHours: 5);
        // 2068: answerable ("already there?") -> auto-answered + resumed.
        WriteSteerPending("card-2068", question: "Is the dark-mode toggle already there?", waitedHours: 5);
        // 2062: a design choice, not answerable from context -> blocked escalation.
        WriteSteerPending("card-2062", question: "Should I also refactor the shared helper?", waitedHours: 5);
        // A fourth card is still inside the timeout -> must keep waiting.
        WriteSteerPending("card-fresh", question: "Is this already implemented?", waitedSeconds: 10);

        var (monitor, scanner) = Build();
        scanner.InvalidateCache();
        var outcomes = await monitor.SweepAsync();

        // Two auto-answered, one blocked, the fresh one untouched.
        Assert.Equal(2, outcomes.Count(o => o.Kind == SteerTimeoutOutcomeKinds.AutoAnswered));
        Assert.Equal(1, outcomes.Count(o => o.Kind == SteerTimeoutOutcomeKinds.Blocked));

        // 2067 / 2068: auto-answered -> left 3-progress for 2-ready, carrying the
        // answer as a pending Continue, marker cleared.
        foreach (var slug in new[] { "card-2067", "card-2068" })
        {
            Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, slug)), $"{slug} must leave 3-progress");
            var ready = Path.Combine(_watchPath, TaskStates.Ready, slug);
            Assert.True(Directory.Exists(ready), $"{slug} must be demoted to 2-ready to resume");
            var intent = Path.Combine(ready, "pending-intent.json");
            Assert.True(File.Exists(intent), $"{slug} must carry the auto-answer as a pending intent");
            Assert.Contains("already integrated", File.ReadAllText(intent));
            Assert.False(File.Exists(Path.Combine(ready, "steer-pending.json")), $"{slug} steer marker must be cleared");
        }

        // 2062: blocked -> escalated to 5e-escalated, marker cleared.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "card-2062")));
        var escalated = Path.Combine(_watchPath, TaskStates.Escalated, "card-2062");
        Assert.True(Directory.Exists(escalated), "2062 must be escalated to 5e-escalated");
        Assert.False(File.Exists(Path.Combine(escalated, "steer-pending.json")), "2062 steer marker must be cleared");

        // The fresh card is still waiting - never acted on before its timeout.
        var fresh = Path.Combine(_watchPath, TaskStates.Progress, "card-fresh");
        Assert.True(Directory.Exists(fresh), "a within-timeout card must keep waiting");
        Assert.True(File.Exists(Path.Combine(fresh, "steer-pending.json")), "the fresh card's marker must remain");

        // Audit + timeline evidence: the resolution is recorded, not silent.
        var audit = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "steer-timeout.jsonl"));
        Assert.Contains("auto-answered", audit);
        Assert.Contains("blocked", audit);

        var timeline2067 = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Ready, "card-2067", "logs", "timeline.jsonl"));
        Assert.Contains("steer_timeout_resolved", timeline2067);
    }

    [Fact]
    public async Task ManualMode_AttendedWait_IsNeverAutoResolved()
    {
        WriteSteerPending("attended", question: "Is this already implemented?", waitedHours: 5);

        var (monitor, scanner) = Build();
        monitor.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus { ProjectName = ProjectName, Mode = "manual" }
            }
        };
        scanner.InvalidateCache();
        var outcomes = await monitor.SweepAsync();

        Assert.Empty(outcomes);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "attended")),
            "a manual-mode (attended) steer wait must not be auto-resolved");
    }

    [Fact]
    public async Task Disabled_IsNoOp()
    {
        WriteSteerPending("card", question: "Is this already implemented?", waitedHours: 5);

        var (monitor, scanner) = Build(enabled: false);
        scanner.InvalidateCache();
        var outcomes = await monitor.SweepAsync();

        Assert.Empty(outcomes);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "card")));
    }

    // --- harness ---------------------------------------------------------

    /// <summary>
    /// Fake resolver mirroring the production default's decision surface without
    /// touching git: it answers exactly the "already implemented?" class (via the
    /// real classifier) and is ambiguous otherwise.
    /// </summary>
    private sealed class FakeResolver : ISteerTimeoutResolver
    {
        public SteerResolveResult Resolve(SteerResolveContext ctx)
            => SteerQuestionClassifier.IsAlreadyImplementedQuestion(ctx.Question)
                ? SteerResolveResult.Answer($"Branch-state check: work already integrated into {ctx.TaskBranch}; finalize with [[TASK_DONE]].")
                : SteerResolveResult.Ambiguous("the follow-up is a design decision not derivable from the task context");
    }

    private (SteerTimeoutMonitor Monitor, TaskScannerService Scanner) Build(bool enabled = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Runner:SteerTimeout:Enabled"] = enabled ? "true" : "false",
            ["Runner:SteerTimeout:TimeoutSeconds"] = "120",
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        var timeline = new AgentStudio.Tasks.TimelineLog(NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        var escalation = new HumanReviewEscalation(states, transitions, _workspaceRoot, NullLogger<HumanReviewEscalation>.Instance, scanner);

        var sp = new ServiceCollection().BuildServiceProvider();

        var monitor = new SteerTimeoutMonitor(
            scanner, transitions, mutations, escalation, settings, chatLog,
            new FakeResolver(), sp, config, taskAccess,
            NullLogger<SteerTimeoutMonitor>.Instance, timeline);
        // Default: treat the project as unattended (auto) so the timeout applies.
        monitor.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus { ProjectName = ProjectName, Mode = "auto-continuous" }
            }
        };
        return (monitor, scanner);
    }

    // (auto-mode status is forced in Build via StatusProviderOverride)

    private void WriteSteerPending(string slug, string question, int? waitedHours = null, int? waitedSeconds = null)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Progress}\",\"order\":1,\"agent\":\"copilot\",\"phase\":\"{LifecyclePhases.SteerPending}\"}}");

        var wait = waitedHours is int h
            ? DateTime.UtcNow - TimeSpan.FromHours(h)
            : DateTime.UtcNow - TimeSpan.FromSeconds(waitedSeconds ?? 0);
        var stamp = wait.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var escaped = question.Replace("\"", "\\\"");
        File.WriteAllText(Path.Combine(dir, SteerPendingMarker.FileName),
            $"{{\"waitStartedAt\":\"{stamp}\",\"kind\":\"steer\",\"question\":\"{escaped}\",\"timeoutSeconds\":0}}");
    }
}
