using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteClaimReplayLanePolicyTests
{
    [Theory]
    [InlineData(TaskStates.Progress, RemoteClaimReplayLaneAction.AlreadyConverged)]
    [InlineData(TaskStates.Ready, RemoteClaimReplayLaneAction.RepairToProgress)]
    [InlineData(TaskStates.AutoReview, RemoteClaimReplayLaneAction.Refuse)]
    [InlineData(TaskStates.Completed, RemoteClaimReplayLaneAction.Refuse)]
    [InlineData(null, RemoteClaimReplayLaneAction.Refuse)]
    public void Decide_requires_replay_to_converge_on_progress(
        string? taskState,
        RemoteClaimReplayLaneAction expected)
    {
        var decision = RemoteClaimReplayLanePolicy.Decide(taskState);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(expected == RemoteClaimReplayLaneAction.Refuse, decision.Message is not null);
    }
}
