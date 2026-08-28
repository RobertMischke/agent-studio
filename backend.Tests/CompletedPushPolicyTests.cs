using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for the pure completed-push classifier. The distinction it
/// draws is the whole point of AGT-2688: an environmental failure earns another
/// sweep, a lineage or fast-forward refusal must not, because its inputs (a
/// fixed SHA against a fixed branch) can never produce a different answer.
/// </summary>
public sealed class CompletedPushPolicyTests
{
    [Theory]
    [InlineData("pushed")]
    [InlineData("already-remote")]
    [InlineData("no-remote")]
    public void Classify_SuccessIsPublishedRegardlessOfStatus(string status)
        => Assert.Equal(
            CompletedPushDisposition.Published,
            CompletedPushPolicy.Classify(success: true, status));

    [Theory]
    [InlineData("lineage-blocked")]
    [InlineData("remote-rejected")]
    [InlineData("sha-not-on-branch")]
    [InlineData("missing-sha")]
    [InlineData("invalid-sha")]
    [InlineData("invalid-branch")]
    public void Classify_PolicyRefusalsAreBlockedSoTheyAreNeverReplayed(string status)
        => Assert.Equal(
            CompletedPushDisposition.Blocked,
            CompletedPushPolicy.Classify(success: false, status));

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("cancelled")]
    [InlineData("repo-missing")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_EnvironmentalFailuresStayRetryable(string? status)
        => Assert.Equal(
            CompletedPushDisposition.Retry,
            CompletedPushPolicy.Classify(success: false, status));

    [Fact]
    public void Classify_IsCaseAndWhitespaceInsensitive()
        => Assert.Equal(
            CompletedPushDisposition.Blocked,
            CompletedPushPolicy.Classify(success: false, "  Lineage-Blocked  "));
}
