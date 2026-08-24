using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2677: the claim endpoint must be able to tell a card that is none of this
/// runner's business from a card that a project misconfiguration is holding
/// back. Only the second kind is written onto the card, and it has to name the
/// build-profile gate so the board stops showing an empty `lastRejection`.
/// </summary>
public sealed class RemoteDispatchEligibilityTests
{
    private const string RunnerId = "runner-01";
    private const string RunnerName = "agent-runner-01";

    [Fact]
    public void AClaimableCard_IsAdmittedWithoutARejection()
    {
        var task = ReadyTask();

        var admission = Evaluate(task, RemoteSettings(profile: null));

        Assert.True(admission.Eligible);
        Assert.Null(admission.RejectionCode);
    }

    [Fact]
    public void AGateBlockedCard_IsRefusedWithTheBuildProfileGateCodeAndCause()
    {
        var task = ReadyTask();
        var project = RemoteSettings(new BuildProfile
        {
            BuildCmds = ["dotnet build QualityStudio.slnx"],
            Status = BuildProfileStatuses.Declared,
        });

        var admission = Evaluate(task, project);

        Assert.False(admission.Eligible);
        Assert.Equal(BuildProfileGate.RejectionCode, admission.RejectionCode);
        Assert.Equal("build-profile-gate", admission.RejectionCode);
        Assert.Contains("not yet validated", admission.RejectionReason);
    }

    [Fact]
    public void AProvenProfile_StopsRefusing()
    {
        var task = ReadyTask();
        var profile = new BuildProfile { BuildCmds = ["dotnet build"], Status = BuildProfileStatuses.PipelineReady };

        Assert.True(Evaluate(task, RemoteSettings(profile)).Eligible);
    }

    [Theory]
    [InlineData("some-other-runner")]
    [InlineData("local")]
    public void ACardRoutedElsewhere_IsRefusedSilently(string executionLocation)
    {
        // Recording these would rewrite every card of every other project on
        // every poll; they are not this runner's business, not a defect.
        var task = ReadyTask();
        var project = RemoteSettings(
            new BuildProfile { BuildCmds = ["dotnet build"], Status = BuildProfileStatuses.Declared })
            with { ExecutionLocation = executionLocation };

        var admission = Evaluate(task, project);

        Assert.False(admission.Eligible);
        Assert.Null(admission.RejectionCode);
        Assert.Null(admission.RejectionReason);
    }

    [Fact]
    public void ACardBlockedByAReference_IsRefusedSilently()
    {
        var blocker = ReadyTask() with { Id = "blocker", Key = "AGT-2", State = TaskStates.Progress };
        var task = ReadyTask() with
        {
            References = new TaskReferences { DependsOn = [new TaskDependencyReference("AGT-2")] },
        };
        var project = RemoteSettings(
            new BuildProfile { BuildCmds = ["dotnet build"], Status = BuildProfileStatuses.Declared });

        var admission = RemoteDispatchEligibility.Evaluate(
            task, project, RunnerId, RunnerName, TaskReferenceIndex.Build([task, blocker]));

        Assert.False(admission.Eligible);
        Assert.Null(admission.RejectionCode);
    }

    [Fact]
    public void IsAssignedAndRunnable_StillFoldsInTheGate()
    {
        var task = ReadyTask();
        var references = TaskReferenceIndex.Build([task]);
        var blocked = RemoteSettings(
            new BuildProfile { BuildCmds = ["dotnet build"], Status = BuildProfileStatuses.Declared });

        Assert.True(RemoteDispatchEligibility.IsAssignedAndRunnableExceptBuildProfile(
            task, blocked, RunnerId, RunnerName, references));
        Assert.False(RemoteDispatchEligibility.IsAssignedAndRunnable(
            task, blocked, RunnerId, RunnerName, references));
        Assert.False(RemoteDispatchEligibility.IsClaimableReady(
            task, blocked, RunnerId, RunnerName, runnerReadOnly: false, references));
    }

    private static RemoteDispatchAdmission Evaluate(TaskInfo task, ProjectSettings project) =>
        RemoteDispatchEligibility.Evaluate(
            task, project, RunnerId, RunnerName, TaskReferenceIndex.Build([task]));

    private static TaskInfo ReadyTask() => new()
    {
        Id = "task-1",
        Key = "QS-100",
        Title = "Waiting card",
        ProjectName = "quality-studio",
        State = TaskStates.Ready,
        Agent = AgentTypes.Codex,
        EnteredLaneAt = new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 8, 18, 7, 0, 0, DateTimeKind.Utc),
    };

    private static ProjectSettings RemoteSettings(BuildProfile? profile) => new()
    {
        PickupMode = PickupModes.Auto,
        ExecutionLocation = RunnerId,
        BuildProfile = profile,
    };
}
