using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for the branching decision that keeps the local integration
/// branch and origin on one lineage. The regression it guards: a diverged local
/// branch could only ever be fast-forwarded, so it never converged, every later
/// delivery merge failed, and the publish was re-driven forever.
/// </summary>
public sealed class IntegrationBranchReconciliationPolicyTests
{
    [Fact]
    public void Decide_TreatsARepositoryWithoutOriginAsTheOnlyLine()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: false,
            localBranchExists: true,
            tipsEqual: false,
            remoteIsAncestorOfLocal: false,
            localIsAncestorOfRemote: false);

        Assert.Equal(IntegrationBranchReconciliationMode.LocalOnly, decision.Mode);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Decide_CreatesTheLocalBranchFromThePublishedTipWhenItIsMissing()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: true,
            localBranchExists: false,
            tipsEqual: false,
            remoteIsAncestorOfLocal: false,
            localIsAncestorOfRemote: false);

        Assert.Equal(IntegrationBranchReconciliationMode.CreateFromRemote, decision.Mode);
    }

    [Fact]
    public void Decide_IsAlreadyCurrentWhenBothTipsAreTheSameCommit()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: true,
            localBranchExists: true,
            tipsEqual: true,
            remoteIsAncestorOfLocal: true,
            localIsAncestorOfRemote: true);

        Assert.Equal(IntegrationBranchReconciliationMode.AlreadyCurrent, decision.Mode);
    }

    [Fact]
    public void Decide_FastForwardsWhenOriginIsStrictlyAhead()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: true,
            localBranchExists: true,
            tipsEqual: false,
            remoteIsAncestorOfLocal: false,
            localIsAncestorOfRemote: true);

        Assert.Equal(IntegrationBranchReconciliationMode.FastForwardFromRemote, decision.Mode);
    }

    [Fact]
    public void Decide_PublishesWhenTheLocalBranchIsStrictlyAhead()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: true,
            localBranchExists: true,
            tipsEqual: false,
            remoteIsAncestorOfLocal: true,
            localIsAncestorOfRemote: false);

        Assert.Equal(IntegrationBranchReconciliationMode.PublishLocal, decision.Mode);
    }

    [Fact]
    public void Decide_ConvergesDivergedLinesByMergingOriginIntoLocal()
    {
        var decision = IntegrationBranchReconciliationPolicy.Decide(
            hasRemote: true,
            localBranchExists: true,
            tipsEqual: false,
            remoteIsAncestorOfLocal: false,
            localIsAncestorOfRemote: false);

        Assert.Equal(IntegrationBranchReconciliationMode.MergeRemoteIntoLocal, decision.Mode);
        Assert.Contains("diverged", decision.Reason);
    }
}
