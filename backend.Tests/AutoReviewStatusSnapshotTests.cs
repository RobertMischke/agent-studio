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
}
