using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewStatusSnapshotTests
{
    [Fact]
    public void Read_TracksParallelActivitiesAndTheirCurrentSteps()
    {
        var snapshot = new AutoReviewStatusSnapshot();
        snapshot.BeginTick();

        snapshot.SetCurrent("project-a", "task-a");
        snapshot.SetCurrentStep("project-a", "task-a", AutoReviewActivitySteps.Aspects);
        snapshot.SetCurrent("project-a", "task-b");
        snapshot.SetCurrentStep("project-a", "task-b", AutoReviewActivitySteps.GateQueued);

        var status = snapshot.Read();

        Assert.Collection(
            status.ActiveJobs.OrderBy(activity => activity.JobId),
            activity =>
            {
                Assert.Equal("task-a", activity.JobId);
                Assert.Equal(AutoReviewActivitySteps.Aspects, activity.Step);
            },
            activity =>
            {
                Assert.Equal("task-b", activity.JobId);
                Assert.Equal(AutoReviewActivitySteps.GateQueued, activity.Step);
            });
    }

    [Fact]
    public void ClearCurrent_RemovesOnlyTheCompletedCard()
    {
        var snapshot = new AutoReviewStatusSnapshot();
        snapshot.BeginTick();
        snapshot.SetCurrent("project-a", "task-a");
        snapshot.SetCurrent("project-a", "task-b");

        snapshot.ClearCurrent("project-a", "task-a");

        var activity = Assert.Single(snapshot.Read().ActiveJobs);
        Assert.Equal("task-b", activity.JobId);
    }

    [Fact]
    public void TickBoundary_PreservesActivityOwnedByParallelWorker()
    {
        var snapshot = new AutoReviewStatusSnapshot();
        snapshot.SetCurrent("project-a", "task-a");
        snapshot.SetCurrentStep("project-a", "task-a", AutoReviewActivitySteps.Aspects);

        snapshot.BeginTick();
        snapshot.EndTick();

        var activity = Assert.Single(snapshot.Read().ActiveJobs);
        Assert.Equal("task-a", activity.JobId);
        Assert.Equal(AutoReviewActivitySteps.Aspects, activity.Step);
    }

    [Fact]
    public void Read_restores_durable_review_activity_and_keeps_a_more_precise_local_step()
    {
        var snapshot = new AutoReviewStatusSnapshot();
        var started = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        snapshot.SetCurrentStep("project-a", "task-a", AutoReviewActivitySteps.Aspects);

        var status = snapshot.Read(
        [
            new AutoReviewActivityView(
                "project-a",
                "task-a",
                AutoReviewActivitySteps.Processing,
                started),
            new AutoReviewActivityView(
                "project-a",
                "task-b",
                AutoReviewActivitySteps.Processing,
                started),
        ]);

        Assert.Equal(2, status.ActiveJobs.Count);
        Assert.Equal(
            AutoReviewActivitySteps.Aspects,
            Assert.Single(status.ActiveJobs, activity => activity.JobId == "task-a").Step);
        Assert.Equal(
            AutoReviewActivitySteps.Processing,
            Assert.Single(status.ActiveJobs, activity => activity.JobId == "task-b").Step);
    }
}
