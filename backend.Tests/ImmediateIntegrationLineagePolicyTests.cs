using Xunit;

namespace AgentStudio.Tests;

public sealed class ImmediateIntegrationLineagePolicyTests
{
    [Theory]
    [InlineData("develop", true, false)]
    [InlineData("integration", true, false)]
    [InlineData("main", false, false)]
    public void Decide_UsesConfiguredTargetWhenDualLineageDoesNotApply(
        string targetBranch,
        bool developAvailable,
        bool mainIsAncestorOfDevelop)
    {
        var decision = ImmediateIntegrationLineagePolicy.Decide(
            targetBranch,
            developAvailable,
            mainIsAncestorOfDevelop);

        Assert.Equal(ImmediateIntegrationLineageMode.DirectToConfiguredTarget, decision.Mode);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Decide_UsesDevelopThenMainWhenReleaseLineIsAnAncestor()
    {
        var decision = ImmediateIntegrationLineagePolicy.Decide(
            "main",
            developAvailable: true,
            mainIsAncestorOfDevelop: true);

        Assert.Equal(ImmediateIntegrationLineageMode.DevelopThenMain, decision.Mode);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Decide_BlocksExistingDivergenceInsteadOfMergingDeliveryIntoMain()
    {
        var decision = ImmediateIntegrationLineagePolicy.Decide(
            "main",
            developAvailable: true,
            mainIsAncestorOfDevelop: false);

        Assert.Equal(ImmediateIntegrationLineageMode.Blocked, decision.Mode);
        Assert.Contains("main is not an ancestor of develop", decision.Reason);
    }

    [Theory]
    [InlineData("develop", true, false)]
    [InlineData("main", false, false)]
    [InlineData("main", true, true)]
    public void DecideDirectMainAdvance_AllowsOnlyNonReleaseTargetsOrPublishedDevelopTip(
        string targetBranch,
        bool developAvailable,
        bool candidateIsPublishedDevelopTip)
    {
        var decision = ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance(
            targetBranch,
            developAvailable,
            candidateIsPublishedDevelopTip);

        Assert.Equal(ImmediateMainAdvanceMode.Allowed, decision.Mode);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void DecideDirectMainAdvance_BlocksRawCandidateInDualLineRepository()
    {
        var decision = ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance(
            "main",
            developAvailable: true,
            candidateIsPublishedDevelopTip: false);

        Assert.Equal(ImmediateMainAdvanceMode.Blocked, decision.Mode);
        Assert.Contains("not the published develop tip", decision.Reason);
    }
}
