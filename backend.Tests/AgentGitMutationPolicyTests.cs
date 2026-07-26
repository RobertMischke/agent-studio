using Xunit;

namespace AgentStudio.Tests;

public sealed class AgentGitMutationPolicyTests
{
    [Fact]
    public void LinearWorkerHeadAdvance_IsInfoAndCleanupEligible()
    {
        var decision = AgentGitMutationPolicy.Decide(
            headBefore: "aaaaaaaa",
            headAfter: "bbbbbbbb",
            headBeforeIsAncestorOfAfter: true,
            preExistingHistoryRewritten: false,
            protectedRemoteChanged: false,
            workerReportedPushOrCommit: true);

        Assert.Equal(AgentGitMutationDisposition.Info, decision.Disposition);
        Assert.True(decision.CleanupEligible);
        Assert.Contains("advanced HEAD", decision.Reason);
    }

    [Fact]
    public void ProtectedBranchPush_IsEscalatedAsGenuineDamage()
    {
        var decision = AgentGitMutationPolicy.Decide(
            headBefore: "aaaaaaaa",
            headAfter: "bbbbbbbb",
            headBeforeIsAncestorOfAfter: true,
            preExistingHistoryRewritten: false,
            protectedRemoteChanged: true,
            workerReportedPushOrCommit: true);

        Assert.Equal(AgentGitMutationDisposition.Escalate, decision.Disposition);
        Assert.False(decision.CleanupEligible);
        Assert.Contains("protected remote branch", decision.Reason);
    }

    [Fact]
    public void RewriteOfPreExistingHistory_IsEscalatedAsGenuineDamage()
    {
        var decision = AgentGitMutationPolicy.Decide(
            headBefore: "aaaaaaaa",
            headAfter: "cccccccc",
            headBeforeIsAncestorOfAfter: false,
            preExistingHistoryRewritten: true,
            protectedRemoteChanged: false,
            workerReportedPushOrCommit: true);

        Assert.Equal(AgentGitMutationDisposition.Escalate, decision.Disposition);
        Assert.False(decision.CleanupEligible);
        Assert.Contains("rewrote history", decision.Reason);
    }

    [Fact]
    public void NonLinearHeadMoveWithoutOriginalBranchRewrite_IsInfoNotEscalation()
    {
        var decision = AgentGitMutationPolicy.Decide(
            headBefore: "aaaaaaaa",
            headAfter: "cccccccc",
            headBeforeIsAncestorOfAfter: false,
            preExistingHistoryRewritten: false,
            protectedRemoteChanged: false,
            workerReportedPushOrCommit: true);

        Assert.Equal(AgentGitMutationDisposition.Info, decision.Disposition);
        Assert.False(decision.CleanupEligible);
        Assert.Contains("advanced HEAD", decision.Reason);
    }
}
