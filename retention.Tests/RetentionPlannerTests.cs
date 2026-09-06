using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(29, 0)]
    [InlineData(30, 1)]
    [InlineData(179, 1)]
    public void StageOneUsesInclusiveAgeBoundary(int ageDays, int actions)
    {
        var plan = Plan(Task(ageDays, "7-archive", [Heavy("logs/cli-output.log", 100)]));
        Assert.Equal(actions, plan.Actions.Count(action => action.Kind == RetentionActionKind.ArchiveHeavy));
    }

    [Fact]
    public void StageTwoArchivesEvidenceAndHeavyButKeepsAuthorityAndStatusStub()
    {
        var plan = Plan(Task(180, "7-archive",
            [Heavy("results/trace.zip", 10), Evidence("report.md", 4), Evidence("status.md", 3), Authority("task.json", 2)]));
        var action = Assert.Single(plan.Actions, action => action.Kind == RetentionActionKind.ArchiveTask);
        Assert.Equal(["results/trace.zip", "report.md"], action.RelativePaths);
    }

    [Theory]
    [InlineData("5-human-review")]
    [InlineData("5e-escalated")]
    [InlineData("3-progress")]
    public void HumanAndActiveLanesNeverArchive(string lane)
    {
        var plan = Plan(Task(400, lane, [Heavy("logs/cli-output.log", 100)]));
        Assert.DoesNotContain(plan.Actions, action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask);
    }

    [Fact]
    public void BudgetOverflowSelectsOldestHeavyFilesFirst()
    {
        const long mib = 1024 * 1024;
        var task = Task(1, "7-archive",
        [
            Heavy("results/new.zip", 30 * mib, Now.AddDays(-1)),
            Heavy("results/old.zip", 40 * mib, Now.AddDays(-3)),
            Heavy("results/middle.zip", 30 * mib, Now.AddDays(-2)),
        ]);
        var action = Assert.Single(Plan(task).Actions, action => action.Kind == RetentionActionKind.ArchiveHeavy);
        Assert.Equal(["results/old.zip"], action.RelativePaths);
    }

    [Fact]
    public void RuntimeAgeBoundariesUseBusAndAttemptAuthorityDefaults()
    {
        var task = new RetentionTaskInventory("runtime", "runtime", "_workspace", "runtime", null,
        [
            Runtime("logs/bus/p/old.jsonl", Now.AddDays(-30)),
            Runtime("logs/bus/p/new.jsonl", Now.AddDays(-29)),
            Runtime(".metadata/attempt-authority.archive-1.json", Now.AddDays(-90)),
            Runtime(".metadata/attempt-authority.archive-2.json", Now.AddDays(-89)),
        ], "/tmp");
        var action = Assert.Single(Plan(task).Actions, action => action.Kind == RetentionActionKind.DeleteRuntime);
        Assert.Equal(["logs/bus/p/old.jsonl", ".metadata/attempt-authority.archive-1.json"], action.RelativePaths);
    }

    private static RetentionPlan Plan(RetentionTaskInventory task) =>
        new RetentionPlanner().Plan(RetentionPolicy.Default(), [task], Now);

    private static RetentionTaskInventory Task(int ageDays, string lane, IReadOnlyList<RetentionFileInventory> files) =>
        new("id", "AGT-1", "Agent", lane, Now.AddDays(-ageDays), files, "/tmp/task");

    private static RetentionFileInventory Heavy(string path, long size, DateTimeOffset? modified = null) =>
        new(path, size, modified ?? Now, ArtifactClass.HeavyWorkingData);
    private static RetentionFileInventory Evidence(string path, long size) =>
        new(path, size, Now, ArtifactClass.Evidence);
    private static RetentionFileInventory Authority(string path, long size) =>
        new(path, size, Now, ArtifactClass.Authority);
    private static RetentionFileInventory Runtime(string path, DateTimeOffset modified) =>
        new(path, 1, modified, ArtifactClass.Runtime);
}
