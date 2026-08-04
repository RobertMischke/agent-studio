using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix coverage for the branching decision behind a review baseline.
/// AGT-2220 proved the card field alone is not a safe source: it still said
/// <c>refs/heads/main</c> long after develop became the working branch, so every
/// merge-base landed on an ancient commit.
/// </summary>
public sealed class ReviewBaselineBranchPolicyTests
{
    [Theory]
    // card, project setting, origin/HEAD -> branch, source, outdated
    [InlineData("refs/heads/main", "develop", "main",
        "develop", ReviewBaselineBranchSource.ProjectSetting, true)]
    [InlineData("refs/heads/develop", "develop", "main",
        "develop", ReviewBaselineBranchSource.ProjectSetting, false)]
    [InlineData(null, "develop", "main",
        "develop", ReviewBaselineBranchSource.ProjectSetting, false)]
    [InlineData("refs/heads/main", null, "develop",
        "develop", ReviewBaselineBranchSource.RepositoryDefault, true)]
    [InlineData("refs/heads/main", "   ", "main",
        "main", ReviewBaselineBranchSource.RepositoryDefault, false)]
    [InlineData("refs/heads/main", null, null,
        "main", ReviewBaselineBranchSource.TaskCard, false)]
    [InlineData(null, null, null,
        "develop", ReviewBaselineBranchSource.Fallback, false)]
    public void Decides_the_integration_line_and_reports_card_staleness(
        string? cardBranch,
        string? projectIntegrationBranch,
        string? repositoryDefaultBranch,
        string expectedBranch,
        ReviewBaselineBranchSource expectedSource,
        bool expectedOutdated)
    {
        var decision = ReviewBaselineBranchPolicy.Decide(
            cardBranch,
            projectIntegrationBranch,
            repositoryDefaultBranch);

        Assert.Equal(expectedBranch, decision.Branch);
        Assert.Equal($"refs/heads/{expectedBranch}", decision.IntegrationRef);
        Assert.Equal(expectedSource, decision.Source);
        Assert.Equal(expectedOutdated, decision.CardOutdated);
    }

    [Theory]
    [InlineData("refs/heads/develop")]
    [InlineData("refs/remotes/origin/develop")]
    [InlineData("origin/develop")]
    [InlineData("develop")]
    public void Reads_every_recorded_ref_spelling_as_the_same_branch(string cardBranch)
    {
        var decision = ReviewBaselineBranchPolicy.Decide(cardBranch, "develop", "main");

        Assert.False(decision.CardOutdated);
        Assert.Equal("develop", decision.CardBranch);
    }

    [Fact]
    public void Treats_branch_names_as_case_sensitive_like_git_does()
    {
        var decision = ReviewBaselineBranchPolicy.Decide("refs/heads/Develop", "develop", null);

        Assert.True(decision.CardOutdated);
        Assert.Equal("develop", decision.Branch);
    }

    [Fact]
    public void Names_both_sides_of_a_stale_card_for_the_timeline_entry()
    {
        var decision = ReviewBaselineBranchPolicy.Decide("refs/heads/main", "develop", null);

        Assert.Contains("main", decision.Rationale, StringComparison.Ordinal);
        Assert.Contains("develop", decision.Rationale, StringComparison.Ordinal);
    }
}
