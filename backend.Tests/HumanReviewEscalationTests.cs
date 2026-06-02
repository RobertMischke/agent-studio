using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Endpoints.Tasks;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the contract behind the bug
/// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
/// a SYSTEM-initiated move into <c>5-human-review</c> through
/// <see cref="HumanReviewEscalation"/> ALWAYS leaves the card with an
/// orchestrator verdict (an <see cref="ReviewDecisionKind.Escalate"/> record in
/// the decision journal, which <see cref="TaskEndpointHelpers.BuildOrchestratorVerdictLookup"/>
/// maps to <c>escalate</c>) AND a non-empty <c>status.md</c>. The watchdog-kill
/// case is the acceptance scenario: simulate a kill on a job in 3-progress, run
/// it through the funnel, expect it parked WITH verdict AND status.
/// </summary>
public sealed class HumanReviewEscalationTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;

    public HumanReviewEscalationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-hre-" + Guid.NewGuid().ToString("N"));
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
        _states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            _scanner, clients, new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, prompts);
        _transitions = new TaskTransitionService(_scanner, _states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var indexCache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(indexCache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    private HumanReviewEscalation BuildFunnel() =>
        new(_states, _transitions, _workspaceRoot, NullLogger<HumanReviewEscalation>.Instance);

    [Fact]
    public async Task EscalateAsync_WatchdogKill_MovesAndRecordsVerdictAndStatus()
    {
        const string jobId = "bug-card-delete-button";
        WriteJob(TaskStates.Progress, jobId);

        var funnel = BuildFunnel();
        var outcome = await funnel.EscalateAsync(
            jobId, _watchPath, ProjectName,
            HumanReviewEscalationCategories.WatchdogKill,
            "CLI exceeded the watchdog deadline");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // (a) folder physically left 3-progress and landed in 5-human-review.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, jobId)),
            "the card must have left 3-progress");
        var parked = Path.Combine(_watchPath, TaskStates.HumanReview, jobId);
        Assert.True(Directory.Exists(parked), "the card must have landed in 5-human-review");

        // (b) verdict: the decision journal carries an Escalate record whose
        // reason encodes the category, and the endpoint-derived verdict reads
        // it back as "escalate" (not null).
        var records = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName);
        var latest = records.LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
        Assert.Contains(HumanReviewEscalationCategories.WatchdogKill, latest.Reason);

        var jobs = _scanner.ScanAllJobs();
        var moved = jobs.Single(j => j.Id == jobId);
        var verdicts = TaskEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, _config);
        Assert.True(verdicts.TryGetValue(moved.TaskKey, out var verdict));
        Assert.Equal("escalate", verdict);

        // (c) status.md is present and non-empty, with the category + a logs pointer.
        var statusPath = Path.Combine(parked, "status.md");
        Assert.True(File.Exists(statusPath), "status.md stub must be written");
        var status = File.ReadAllText(statusPath);
        Assert.False(string.IsNullOrWhiteSpace(status));
        Assert.Contains(HumanReviewEscalationCategories.WatchdogKill, status);
        Assert.Contains("logs/decisions", status);
    }

    [Fact]
    public void Escalate_Sync_PickupZombie_MovesAndRecordsVerdictAndStatus()
    {
        const string jobId = "zombie-card";
        WriteJob(TaskStates.Progress, jobId);

        var funnel = BuildFunnel();
        var outcome = funnel.Escalate(
            jobId, _watchPath, ProjectName,
            HumanReviewEscalationCategories.PickupZombie,
            "Resume budget exhausted on a session-less folder");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, jobId)));

        var latest = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName).LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
        Assert.Contains(HumanReviewEscalationCategories.PickupZombie, latest.Reason);

        var status = File.ReadAllText(Path.Combine(_watchPath, TaskStates.HumanReview, jobId, "status.md"));
        Assert.False(string.IsNullOrWhiteSpace(status));
    }

    [Fact]
    public void RecordVerdictAndStatus_NeverClobbersAnExistingSummary()
    {
        const string jobId = "already-summarised";
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, jobId);
        Directory.CreateDirectory(folder);
        const string realSummary = "# Status\n\n- Result: Implemented the feature and added tests.\n";
        File.WriteAllText(Path.Combine(folder, "status.md"), realSummary);

        var funnel = BuildFunnel();
        funnel.RecordVerdictAndStatus(
            ProjectName, jobId, folder,
            HumanReviewEscalationCategories.UnknownLegacy, "legacy repair");

        // The genuine summary survives; only the verdict half is added.
        Assert.Equal(realSummary, File.ReadAllText(Path.Combine(folder, "status.md")));
        var latest = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName).LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
    }
}
