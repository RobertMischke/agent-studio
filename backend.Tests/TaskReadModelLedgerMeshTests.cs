using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// T2b (ASS-1740) Part 3: the unified <see cref="TaskReadModel"/> meshes each
/// append-only <c>lane_changed</c> ledger row (von / nach / wann / ausloeser)
/// with the ASS-1724 commit-provenance anchor recorded for the same crossing,
/// so a single consumer sees the whole lane move - including branch-tip and
/// work-branch-head - without re-reading <c>task.json</c>. The branch SHAs are
/// NOT duplicated into the ledger on disk; the join happens at READ time, by
/// target lane plus nearest timestamp (a task can cross the same lane more than
/// once). Every other event kind passes through untouched.
/// </summary>
public class TaskReadModelLedgerMeshTests
{
    private static TaskReadModel Build(
        IReadOnlyList<TimelineEvent> ledger,
        IReadOnlyList<TaskProvenanceTransition> transitions)
    {
        var detail = new TaskDetail
        {
            Info = new TaskInfo
            {
                Id = "demo",
                FolderPath = "/tmp/demo",
                Provenance = new TaskProvenance { Transitions = [.. transitions] },
            },
        };
        return new TaskReadModel(detail, [], [], ledger, DateTime.UtcNow);
    }

    private static TimelineEvent LaneChange(DateTime ts, string from, string to) => new()
    {
        Ts = ts,
        Kind = TimelineEventKinds.LaneChanged,
        Actor = TimelineActors.System,
        Summary = $"{from} → {to}",
        Details = new Dictionary<string, string> { ["from"] = from, ["to"] = to },
    };

    [Fact]
    public void BuildLedger_MeshesLaneChangedWithMatchingAnchor()
    {
        var at = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var model = Build(
            ledger: [LaneChange(at, TaskStates.AutoReview, TaskStates.HumanReview)],
            transitions:
            [
                new TaskProvenanceTransition
                {
                    Lane = TaskStates.HumanReview,
                    AtUtc = at.AddMilliseconds(40),
                    BranchTip = "abc123",
                    WorkBranchHead = "def456",
                },
            ]);

        var meshed = Assert.Single(model.BuildLedger());
        Assert.Equal(TaskStates.AutoReview, meshed.Details!["from"]);
        Assert.Equal(TaskStates.HumanReview, meshed.Details!["to"]);
        Assert.Equal("abc123", meshed.Details!["branchTip"]);
        Assert.Equal("def456", meshed.Details!["workBranchHead"]);
    }

    [Fact]
    public void BuildLedger_NonLaneChangedEvent_PassesThroughUntouched()
    {
        var at = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var runStarted = new TimelineEvent
        {
            Ts = at,
            Kind = TimelineEventKinds.AgentRunStarted,
            Actor = TimelineActors.Agent,
            Summary = "run 1 started",
        };
        var model = Build(
            ledger: [runStarted],
            transitions:
            [
                new TaskProvenanceTransition
                {
                    Lane = TaskStates.HumanReview,
                    AtUtc = at,
                    BranchTip = "abc123",
                },
            ]);

        var passed = Assert.Single(model.BuildLedger());
        Assert.Same(runStarted, passed);
        Assert.Null(passed.Details);
    }

    [Fact]
    public void BuildLedger_RepeatSameLaneCrossing_PicksNearestTimestampAnchor()
    {
        var first = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var second = first.AddHours(3);
        var model = Build(
            ledger:
            [
                LaneChange(first, TaskStates.AutoReview, TaskStates.HumanReview),
                LaneChange(second, TaskStates.AutoReview, TaskStates.HumanReview),
            ],
            transitions:
            [
                new TaskProvenanceTransition
                {
                    Lane = TaskStates.HumanReview,
                    AtUtc = first.AddMilliseconds(20),
                    BranchTip = "first-tip",
                },
                new TaskProvenanceTransition
                {
                    Lane = TaskStates.HumanReview,
                    AtUtc = second.AddMilliseconds(20),
                    BranchTip = "second-tip",
                },
            ]);

        var rows = model.BuildLedger();
        Assert.Equal(2, rows.Count);
        Assert.Equal("first-tip", rows[0].Details!["branchTip"]);
        Assert.Equal("second-tip", rows[1].Details!["branchTip"]);
    }

    [Fact]
    public void BuildLedger_NoAnchors_LeavesLaneChangedUnchanged()
    {
        var at = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var model = Build(
            ledger: [LaneChange(at, TaskStates.Ready, TaskStates.Progress)],
            transitions: []);

        var row = Assert.Single(model.BuildLedger());
        Assert.False(row.Details!.ContainsKey("branchTip"));
        Assert.False(row.Details!.ContainsKey("workBranchHead"));
        Assert.Equal(TaskStates.Progress, row.Details!["to"]);
    }
}
