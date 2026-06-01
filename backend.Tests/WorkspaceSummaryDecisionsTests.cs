using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.State;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the executive-summary decisions fold: WorkspaceSummaryService
/// reads the per-project decision journal written by ReviewDecisionLog and
/// surfaces it as per-project decisionsMade counts plus a workspace-wide
/// topDecisions list ranked by severity then recency. Exercises the real
/// producer (ReviewDecisionLog.Append) so the round-trip is covered.
/// </summary>
public class WorkspaceSummaryDecisionsTests : IDisposable
{
    private const string Project = "test";
    private readonly string _root;
    private readonly string _projectPath;

    public WorkspaceSummaryDecisionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-taskboard-summary-" + Guid.NewGuid().ToString("N"));
        _projectPath = Path.Combine(_root, "projects", Project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_projectPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private WorkspaceSummaryService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _projectPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new WorkspaceSummaryService(
            scanner,
            new SupervisorAdvisoryStore(),
            config,
            NullLogger<WorkspaceSummaryService>.Instance);
    }

    private void AppendDecision(ReviewDecisionKind kind, DateTime atUtc, string jobId = "some-job", string reason = "because")
        => ReviewDecisionLog.Append(_root, new ReviewDecisionRecord(
            CreatedAt: atUtc,
            JobId: jobId,
            Project: Project,
            Kind: kind,
            Reason: reason,
            Prompt: "p",
            Response: "r",
            FollowUp: ""));

    [Fact]
    public void Decisions_InWindow_CountedAndRankedBySeverity()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        AppendDecision(ReviewDecisionKind.AcceptAsDone, now.AddMinutes(-30), "job-a", "accepted as done");
        AppendDecision(ReviewDecisionKind.Escalate, now.AddMinutes(-20), "job-b", "escalated to human");
        AppendDecision(ReviewDecisionKind.Reissue, now.AddMinutes(-10), "job-c", "reissued with stronger framing");

        var summary = Build().Build(24, now);

        var project = Assert.Single(summary.ByProject);
        Assert.Equal(3, project.DecisionsMade);

        // Ranked High > Warn > Info.
        Assert.Equal(3, summary.TopDecisions.Count);
        Assert.Equal("High", summary.TopDecisions[0].Severity);
        Assert.Equal("job-b", summary.TopDecisions[0].JobId);
        Assert.Equal("Warn", summary.TopDecisions[1].Severity);
        Assert.Equal("Info", summary.TopDecisions[2].Severity);
        // Each reference locates its source line.
        Assert.All(summary.TopDecisions, d => Assert.Contains("@", d.DecisionId));
    }

    [Fact]
    public void Decisions_OutsideWindow_AreExcluded()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        AppendDecision(ReviewDecisionKind.Escalate, now.AddHours(-48), "old-job");
        AppendDecision(ReviewDecisionKind.Reissue, now.AddHours(-1), "recent-job");

        var summary = Build().Build(24, now);

        var project = Assert.Single(summary.ByProject);
        Assert.Equal(1, project.DecisionsMade);
        var decision = Assert.Single(summary.TopDecisions);
        Assert.Equal("recent-job", decision.JobId);
    }

    [Fact]
    public void MissingJournal_YieldsZeroDecisions_NoThrow()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);

        var summary = Build().Build(24, now);

        Assert.Empty(summary.TopDecisions);
        Assert.DoesNotContain(summary.ByProject, p => p.DecisionsMade > 0);
    }

    [Fact]
    public void TopDecisions_CappedAtTen()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 15; i++)
            AppendDecision(ReviewDecisionKind.Reissue, now.AddMinutes(-i - 1), $"job-{i}");

        var summary = Build().Build(24, now);

        Assert.Equal(15, Assert.Single(summary.ByProject).DecisionsMade);
        Assert.Equal(10, summary.TopDecisions.Count);
    }

    [Fact]
    public void BlankReason_FallsBackToKindAndJobIdTitle()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        AppendDecision(ReviewDecisionKind.Escalate, now.AddMinutes(-5), "lonely-job", reason: "");

        var summary = Build().Build(24, now);

        var decision = Assert.Single(summary.TopDecisions);
        Assert.Equal("Escalate lonely-job", decision.Title);
    }
}
