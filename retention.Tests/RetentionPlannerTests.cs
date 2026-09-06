using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Archive_age_boundary_is_inclusive()
    {
        Assert.Empty(Plan(Task("7-archive", Now.AddDays(-30).AddTicks(1), Heavy("logs/cli-output.log", 10))).Tasks);
        Assert.Equal(RetentionActionKind.ArchiveHeavy,
            Assert.Single(Assert.Single(Plan(Task("7-archive", Now.AddDays(-30), Heavy("logs/cli-output.log", 10))).Tasks).Actions).Kind);
    }

    [Theory]
    [InlineData("0-inbox")]
    [InlineData("3-progress")]
    [InlineData("5-human-review")]
    [InlineData("5e-escalated")]
    public void Never_archive_lanes_are_excluded(string lane) =>
        Assert.Empty(Plan(Task(lane, Now.AddDays(-400), Heavy("results/trace.zip", 10))).Tasks);

    [Fact]
    public void Whole_task_stage_begins_at_180_days_and_keeps_authority_and_status()
    {
        var task = Task("7-archive", Now.AddDays(-180),
            Heavy("logs/cli-output.log", 10),
            File("report.md", 20, ArtifactClass.Evidence, Now),
            File("status.md", 30, ArtifactClass.Evidence, Now),
            File("task.json", 40, ArtifactClass.Authority, Now));
        var action = Assert.Single(Assert.Single(Plan(task).Tasks).Actions);
        Assert.Equal(RetentionActionKind.ArchiveTask, action.Kind);
        Assert.Equal(new[] { "logs/cli-output.log", "report.md" }, action.RelativePaths);
    }

    [Fact]
    public void Budget_overflow_archives_oldest_heavy_files_first()
    {
        var mib = RetentionPolicy.MiB;
        var task = Task("6-completed", Now.AddDays(-1),
            File("results/old.trace", 40 * mib, ArtifactClass.HeavyWorkingData, Now.AddDays(-3)),
            File("results/new.trace", 40 * mib, ArtifactClass.HeavyWorkingData, Now.AddDays(-1)));
        var action = Assert.Single(Assert.Single(Plan(task).Tasks).Actions);
        Assert.Equal(RetentionActionKind.ArchiveHeavy, action.Kind);
        Assert.Equal(new[] { "results/old.trace" }, action.RelativePaths);
    }

    [Fact]
    public void Runtime_family_boundaries_keep_live_authority_and_apply_7_30_90_day_defaults()
    {
        var task = new RetentionTaskInventory(
            "__workspace-runtime__", "runtime", "__workspace__", "runtime", null, "/tmp",
            [
                File("logs/bus/old.jsonl", 1, ArtifactClass.Runtime, Now.AddDays(-30)),
                File("logs/cli-output.log.1", 2, ArtifactClass.Runtime, Now.AddDays(-7)),
                File(".metadata/attempt-authority-2026-01-01.json", 3, ArtifactClass.Runtime, Now.AddDays(-90)),
                File(".metadata/attempt-authority.json", 4, ArtifactClass.Runtime, Now.AddDays(-900)),
            ]);
        var action = Assert.Single(Assert.Single(Plan(task).Tasks).Actions);
        Assert.Equal(RetentionActionKind.DeleteRuntime, action.Kind);
        Assert.Equal(6, action.Bytes);
        Assert.DoesNotContain(".metadata/attempt-authority.json", action.RelativePaths);
    }

    private static RetentionPlan Plan(RetentionTaskInventory task) => RetentionPlanner.Plan([task], RetentionPolicy.Default, Now);
    private static RetentionTaskInventory Task(string lane, DateTimeOffset? terminalAt, params RetentionFileInventory[] files) =>
        new("AGT-1", "id-1", "PROJ-1", lane, terminalAt, "/tmp/task", files);
    private static RetentionFileInventory Heavy(string path, long size) => File(path, size, ArtifactClass.HeavyWorkingData, Now);
    private static RetentionFileInventory File(string path, long size, ArtifactClass type, DateTimeOffset modified) =>
        new(path, size, modified, type);
}
