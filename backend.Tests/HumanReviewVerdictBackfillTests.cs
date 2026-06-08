using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the migration half of the bug
/// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
/// the boot-time sweep <see cref="ReviewDecisionOrchestrator.BackfillVerdictlessHumanReview"/>
/// gives every <c>5-human-review</c> card that carries NO decision-journal
/// record a retroactive <see cref="ReviewDecisionKind.Escalate"/> verdict
/// (category <see cref="HumanReviewEscalationCategories.UnknownLegacy"/>) and a
/// <c>status.md</c> stub, while leaving already-explained cards untouched. The
/// sweep is idempotent: a second run is a no-op.
/// </summary>
public sealed class HumanReviewVerdictBackfillTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ReviewDecisionOrchestrator _orchestrator;

    public HumanReviewVerdictBackfillTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-hrvb-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            _scanner, clients, new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, prompts);
        var transitions = new TaskTransitionService(_scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var indexCache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            _scanner, mutations, states, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var aspectRunner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var funnel = new HumanReviewEscalation(states, transitions, _workspaceRoot, NullLogger<HumanReviewEscalation>.Instance);

        _orchestrator = new ReviewDecisionOrchestrator(
            _scanner, states, taskAccess, chatLog, prompts, aspectRunner,
            new AutoReviewStatusSnapshot(), _config,
            NullLogger<ReviewDecisionOrchestrator>.Instance,
            humanReviewEscalation: funnel);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Backfill_RepairsVerdictlessCard_AndLeavesExplainedCardUntouched()
    {
        // Legacy card: parked in 5-human-review with NO verdict and no status.
        WriteJob(TaskStates.HumanReview, "legacy-verdictless");
        // Already-explained card: carries a prior accept verdict + a real summary.
        WriteJob(TaskStates.HumanReview, "already-explained");
        ReviewDecisionLog.Append(_workspaceRoot, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow, JobId: "already-explained", Project: ProjectName,
            Kind: ReviewDecisionKind.AcceptAsDone, Reason: "looks good", Prompt: "p", Response: "r", FollowUp: ""));
        const string realSummary = "# Status\n\n- Result: done well.\n";
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "already-explained", "status.md"), realSummary);

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        // Legacy card got an escalate verdict (unknown-legacy) + a status stub.
        var legacy = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "legacy-verdictless").ToList();
        Assert.Single(legacy);
        Assert.Equal(ReviewDecisionKind.Escalate, legacy[0].Kind);
        Assert.Contains(HumanReviewEscalationCategories.UnknownLegacy, legacy[0].Reason);
        var stub = File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "legacy-verdictless", "status.md"));
        Assert.False(string.IsNullOrWhiteSpace(stub));

        // Explained card untouched: no extra verdict, summary preserved.
        var explained = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "already-explained").ToList();
        Assert.Single(explained);
        Assert.Equal(ReviewDecisionKind.AcceptAsDone, explained[0].Kind);
        Assert.Equal(realSummary,
            File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, "already-explained", "status.md")));
    }

    [Fact]
    public void Backfill_IsIdempotent_SecondRunAddsNoNewRecords()
    {
        WriteJob(TaskStates.HumanReview, "legacy-verdictless");

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);
        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        var records = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName)
            .Where(r => r.JobId == "legacy-verdictless").ToList();
        Assert.Single(records);
    }

    [Fact]
    public void Backfill_DoesNotTouchCardsOutsideHumanReview()
    {
        WriteJob(TaskStates.AutoReview, "in-auto-review");
        WriteJob(TaskStates.Progress, "in-progress");

        _orchestrator.BackfillVerdictlessHumanReview(_workspaceRoot, CancellationToken.None);

        Assert.Empty(ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName));
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
    }
}
