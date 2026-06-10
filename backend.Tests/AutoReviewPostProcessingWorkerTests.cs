using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewPostProcessingWorkerTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace;
    private readonly string _watchPath;

    public AutoReviewPostProcessingWorkerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "auto-review-post-processing-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProcessAsync_DrainsQueuedAutoReviewCardWithoutWaitingForRecurringTick()
    {
        SeedNoOpReviewJob("noop-task");
        var deps = BuildDeps();
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);

        await worker.ProcessAsync(new AutoReviewPostProcessingRequest(
            ProjectName: Project,
            JobId: "noop-task",
            WatchPath: _watchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: "test"),
            CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "noop-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "noop-task")));
        var followUp = Path.Combine(_watchPath, TaskStates.Ready, "noop-task", "orchestrator-follow-up.md");
        Assert.True(File.Exists(followUp));
    }

    [Fact]
    public async Task ProcessAsync_WhenReviewDecisionOrchestratorDisabled_LeavesQueuedCardInAutoReview()
    {
        SeedNoOpReviewJob("disabled-task");
        var deps = BuildDeps(reviewDecisionEnabled: false);
        var worker = new AutoReviewPostProcessingWorker(
            deps.Queue,
            deps.Orchestrator,
            deps.Scanner,
            deps.Mutations,
            deps.Configuration,
            NullLogger<AutoReviewPostProcessingWorker>.Instance);

        await worker.ProcessAsync(new AutoReviewPostProcessingRequest(
            ProjectName: Project,
            JobId: "disabled-task",
            WatchPath: _watchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: "test"),
            CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "disabled-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "disabled-task")));
        var followUp = Path.Combine(_watchPath, TaskStates.AutoReview, "disabled-task", "orchestrator-follow-up.md");
        Assert.False(File.Exists(followUp));
    }

    private Deps BuildDeps(bool reviewDecisionEnabled = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["ReviewDecisionOrchestrator:Enabled"] = reviewDecisionEnabled ? "true" : "false",
                ["ReviewDecisionOrchestrator:CallsPerHour"] = "100"
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(
            scanner,
            stateMachine,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new AgentStudio.TaskAccess.TaskAccessService(
            scanner,
            mutations,
            stateMachine,
            transitions,
            indexCache,
            NullLogger<AgentStudio.TaskAccess.TaskAccessService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner,
            stateMachine,
            taskAccess,
            new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance),
            prompts,
            new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance),
            new AutoReviewStatusSnapshot(),
            config,
            NullLogger<ReviewDecisionOrchestrator>.Instance);
        orchestrator.CliRunner = (_, _, _, _, _) => Task.FromResult("");

        return new Deps(config, scanner, mutations, new AutoReviewPostProcessingQueue(), orchestrator);
    }

    private void SeedNoOpReviewJob(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"),
            "# Implement cache refresh\n\nAdd a deterministic cache refresh for task reads after a lane transition, and verify the next read observes the moved card.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NOOP]]{Environment.NewLine}");
    }

    private sealed record Deps(
        IConfiguration Configuration,
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        AutoReviewPostProcessingQueue Queue,
        ReviewDecisionOrchestrator Orchestrator);
}
