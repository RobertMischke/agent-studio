using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// T2b (ASS-1740) Part 1: every lane crossing must append one
/// <see cref="TimelineEventKinds.LaneChanged"/> row to the task's
/// <c>logs/timeline.jsonl</c> ledger - the append-only transition HISTORY that
/// the single <c>enteredLaneAt</c> field on <c>task.json</c> could never hold
/// (it only ever remembers the latest move). The row carries von / nach (in
/// <see cref="TimelineEvent.Details"/>), wann (the event <c>Ts</c>), and the
/// ausloeser (the <see cref="TimelineEvent.Actor"/>).
/// </summary>
public class TaskLaneChangeLedgerTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskLaneChangeLedgerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-lanechange-tests-" + Guid.NewGuid().ToString("N"));
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
    public void MoveJob_AppendsLaneChangedRow_WithFromToAndTrigger()
    {
        SeedJob(TaskStates.AutoReview, "promote-me");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob(
            "promote-me", TaskStates.HumanReview, _watchPath, cause: TimelineActors.Human("alice@example.com"));

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);

        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        var laneChange = Assert.Single(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal("human:alice@example.com", laneChange.Actor);
        Assert.NotNull(laneChange.Details);
        Assert.Equal(TaskStates.AutoReview, laneChange.Details!["from"]);
        Assert.Equal(TaskStates.HumanReview, laneChange.Details!["to"]);
        Assert.Contains(TaskStates.HumanReview, laneChange.Summary);
    }

    [Fact]
    public void MoveJob_NullCause_RecordsSystemActor()
    {
        SeedJob(TaskStates.Ready, "auto-pick");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob("auto-pick", TaskStates.Progress, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        var laneChange = Assert.Single(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TimelineActors.System, laneChange.Actor);
    }

    [Fact]
    public void MoveJob_NoOp_DoesNotAppendLaneChangedRow()
    {
        SeedJob(TaskStates.HumanReview, "already-here");

        var (machine, _, timeline) = BuildMachine();
        var outcome = machine.MoveJob("already-here", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var rows = timeline.ReadAll(outcome.NewFolderPath!);
        Assert.DoesNotContain(rows, r => r.Kind == TimelineEventKinds.LaneChanged);
    }

    [Fact]
    public void MoveJob_MultipleCrossings_AccumulateAppendOnlyHistory()
    {
        SeedJob(TaskStates.Ready, "round-trip");

        var (machine, _, timeline) = BuildMachine();
        var toProgress = machine.MoveJob("round-trip", TaskStates.Progress, _watchPath);
        var toReview = machine.MoveJob("round-trip", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, toReview.Status);
        var rows = timeline.ReadAll(toReview.NewFolderPath!);
        var laneChanges = rows.Where(r => r.Kind == TimelineEventKinds.LaneChanged).ToList();
        Assert.Equal(2, laneChanges.Count);
        Assert.Equal(TaskStates.Progress, laneChanges[0].Details!["to"]);
        Assert.Equal(TaskStates.AutoReview, laneChanges[1].Details!["to"]);
    }

    private void SeedJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private (TaskStateMachine, TaskScannerService, TimelineLog) BuildMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        return (machine, scanner, timeline);
    }
}
