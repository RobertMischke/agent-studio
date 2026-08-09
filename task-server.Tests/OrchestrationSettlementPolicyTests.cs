using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class OrchestrationSettlementPolicyTests
{
    private static readonly OrchestrationStage[] Stages =
        OrchestrationDefaults.CreateStages().ToArray();

    [Theory]
    [InlineData(OrchestrationAction.Reissue, 0, 2, "reissued", "2-ready", 1)]
    [InlineData(OrchestrationAction.Reissue, 2, 2, "escalated", "5e-escalated", 3)]
    [InlineData(OrchestrationAction.Escalate, 0, 2, "escalated", "5e-escalated", 0)]
    [InlineData(OrchestrationAction.Complete, 0, 2, "completed", "5-human-review", 0)]
    [InlineData(OrchestrationAction.Fail, 0, 2, "failed", "5e-escalated", 0)]
    public void Terminal_actions_choose_one_bounded_lane_effect(
        OrchestrationAction action,
        int currentReissues,
        int maxReissues,
        string expectedStatus,
        string expectedLane,
        int expectedReissues)
    {
        var decision = OrchestrationSettlementPolicy.Decide(
            action,
            Stages,
            OrchestrationStage.ReviewDecision,
            currentReissues,
            maxReissues,
            "4-auto-review",
            7,
            7);

        Assert.True(decision.IsTerminal);
        Assert.Equal(expectedStatus, decision.RunStatus);
        Assert.Equal(expectedLane, decision.TaskState);
        Assert.Equal(expectedReissues, decision.ReissueAttempts);
    }

    [Fact]
    public void Continue_advances_without_mutating_the_task_lane()
    {
        var decision = OrchestrationSettlementPolicy.Decide(
            OrchestrationAction.Continue,
            Stages,
            OrchestrationStage.ReviewDecision,
            0,
            2,
            "4-auto-review",
            7,
            7);

        Assert.False(decision.IsTerminal);
        Assert.Equal("pending", decision.RunStatus);
        Assert.Equal(OrchestrationStage.Council, decision.NextStage);
        Assert.Null(decision.TaskState);
    }

    [Theory]
    [InlineData("5-human-review", 7, 7)]
    [InlineData("4-auto-review", 7, 8)]
    public void Stale_task_authority_is_superseded_without_a_lane_write(
        string taskState,
        long expectedVersion,
        long currentVersion)
    {
        var decision = OrchestrationSettlementPolicy.Decide(
            OrchestrationAction.Complete,
            Stages,
            OrchestrationStage.CompletionJudge,
            0,
            2,
            taskState,
            expectedVersion,
            currentVersion);

        Assert.True(decision.IsTerminal);
        Assert.Equal("superseded", decision.RunStatus);
        Assert.Null(decision.TaskState);
        Assert.NotNull(decision.SupersededReason);
    }
}
