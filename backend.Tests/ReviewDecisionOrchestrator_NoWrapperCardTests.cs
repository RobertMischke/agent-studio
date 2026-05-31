using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0049 acceptance test: when the orchestrator cannot decide a
/// 4-auto-review task unattended (the agent emitted TASK_NEEDS_INPUT and the
/// fast model returns <c>action=escalate</c>), it must keep the task
/// self-contained. The single original card flips to 1b-needs-human-review
/// and records one <c>orchestrator_escalated</c> event on its own timeline.
/// It must NOT spawn a sibling <c>human-decision-needed-&lt;slug&gt;</c> wrapper
/// card in 1-preparation - that wrapper-card pattern (ASS-30) is the bug this
/// ADR ends. This fixture is deliberately self-contained so the contract is
/// discoverable in one file.
/// </summary>
public class ReviewDecisionOrchestrator_NoWrapperCardTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);
    private const string Project = "demo";

    public ReviewDecisionOrchestrator_NoWrapperCardTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-nowrapper-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task OrchestratorCannotDecide_KeepsTaskSelfContained_NoWrapperCard()
    {
        SeedReviewJobWithNeedsInput("payment-flow", "Stripe or Adyen?");
        var orchestrator = BuildOrchestrator(
            cliResponse: "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs a product call on the provider.]]");

        await orchestrator.TickOnceAsync(_workspace, CancellationToken.None);

        // 1) The original card moved OUT of 4-auto-review and INTO
        //    1b-needs-human-review. Same id, same folder, no clone.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "payment-flow")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.NeedsHumanReview, "payment-flow")));

        // 2) No sibling wrapper card was spawned in 1-preparation. We assert
        //    both the specific legacy slug AND that the lane gained no cards.
        Assert.False(Directory.Exists(
            Path.Combine(_watchPath, TaskStates.Preparation, "human-decision-needed-payment-flow")));
        var prepDir = Path.Combine(_watchPath, TaskStates.Preparation);
        Assert.Empty(Directory.GetDirectories(prepDir));

        // 3) The original card's timeline carries exactly one
        //    orchestrator_escalated event explaining the hand-off.
        var events = _timeline.ReadAll(
            Path.Combine(_watchPath, TaskStates.NeedsHumanReview, "payment-flow"));
        var escalate = Assert.Single(
            events.Where(e => e.Kind == TimelineEventKinds.OrchestratorEscalated).ToList());
        Assert.Equal(TimelineActors.Orchestrator, escalate.Actor);
        Assert.Contains("product call", escalate.Details?["reason"]);
    }

    private void SeedReviewJobWithNeedsInput(string slug, string reason)
    {
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{TaskStates.AutoReview}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n\nDo the thing.\n");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"),
            $"[12:00:00.000] [stdout] starting{Environment.NewLine}" +
            $"[12:00:01.000] [stdout] [[TASK_NEEDS_INPUT: {reason}]]{Environment.NewLine}");
    }

    private ReviewDecisionOrchestrator BuildOrchestrator(string cliResponse)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["ReviewDecisionOrchestrator:Enabled"] = "true",
                ["ReviewDecisionOrchestrator:CallsPerHour"] = "100"
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var stateMachine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var statusSnapshot = new AutoReviewStatusSnapshot();
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(scanner, stateMachine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, stateMachine, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);
        var orchestrator = new ReviewDecisionOrchestrator(
            scanner, stateMachine, taskAccess, chatLog, prompts, aspectRunner, statusSnapshot, config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            timeline: _timeline);
        orchestrator.CliRunner = (cli, model, prompt, timeout, ct) => Task.FromResult(cliResponse);
        return orchestrator;
    }
}
