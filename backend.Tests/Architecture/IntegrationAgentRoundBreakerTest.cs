using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture breaker for loop-inventory entry
/// <c>integration.attribution-agent-round</c>. An ambiguous mechanical rebase
/// may open one automatic steer round, but a repeat in the same operator-owned
/// review epoch terminates in Human Review.
/// </summary>
public sealed class IntegrationAgentRoundBreakerTest
{
    [Fact]
    public void Budget_AllowsExactlyOneAutomaticRound()
    {
        Assert.Equal(1, RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds);
        Assert.Equal(
            RemoteIntegrationContinuationAction.StartAgentRound,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                automaticAgentRoundsUsed: 0));
        Assert.Equal(
            RemoteIntegrationContinuationAction.LeaveForHumanReview,
            RemoteIntegrationContinuationPolicy.Decide(
                MergeIntoIntegrationOutcome.AgentRoundRequired,
                automaticAgentRoundsUsed: 1));
    }

    [Fact]
    public void OtherIntegrationOutcomes_NeverOpenThisLoop()
    {
        foreach (var outcome in Enum.GetValues<MergeIntoIntegrationOutcome>()
                     .Where(outcome => outcome != MergeIntoIntegrationOutcome.AgentRoundRequired))
        {
            Assert.Equal(
                RemoteIntegrationContinuationAction.None,
                RemoteIntegrationContinuationPolicy.Decide(outcome, automaticAgentRoundsUsed: 0));
        }
    }
}
