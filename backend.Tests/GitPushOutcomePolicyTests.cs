using AgentStudio.Git;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the push-status vocabulary. The whole AGT-2688 loop came
/// from treating one permanent refusal as if it were a transient fault, so the
/// "can clear on its own" / "can never clear" split is pinned here explicitly.
/// </summary>
public sealed class GitPushOutcomePolicyTests
{
    [Theory]
    [InlineData("pushed")]
    [InlineData("already-remote")]
    [InlineData("no-remote")]
    public void SuccessfulResult_IsPublished(string status)
    {
        var result = new GitPushResult(true, "abc123", status, null);

        Assert.Equal(GitPushReaction.Published, GitPushOutcomePolicy.Decide(result));
        Assert.False(GitPushOutcomePolicy.IsStructurallyBlocked(result));
    }

    // The refusals that no retry can ever clear: the branch topology forbids the
    // advance, or the request names something this repository does not have.
    [Theory]
    [InlineData("lineage-blocked")]
    [InlineData("invalid-sha")]
    [InlineData("invalid-branch")]
    [InlineData("missing-sha")]
    [InlineData("sha-not-on-branch")]
    public void StructuralRefusal_IsBlocked(string status)
    {
        var result = new GitPushResult(false, "abc123", status, "refused");

        Assert.Equal(GitPushReaction.Blocked, GitPushOutcomePolicy.Decide(result));
        Assert.True(GitPushOutcomePolicy.IsStructurallyBlocked(result));
    }

    // Environment and timing faults: the remote was unreachable, had moved on,
    // could not be inspected, or the attempt was cut short by shutdown. A later
    // sweep is meaningful, so these must NOT be marked terminal.
    [Theory]
    [InlineData("failed")]
    [InlineData("remote-rejected")]
    [InlineData("lineage-check-failed")]
    [InlineData("repo-missing")]
    [InlineData("missing-branch")]
    [InlineData("cancelled")]
    [InlineData("error")]
    public void TransientFault_IsRetryable(string status)
    {
        var result = new GitPushResult(false, "abc123", status, "boom");

        Assert.Equal(GitPushReaction.RetryLater, GitPushOutcomePolicy.Decide(result));
        Assert.False(GitPushOutcomePolicy.IsStructurallyBlocked(result));
    }

    [Fact]
    public void UnknownFailureStatus_DefaultsToRetryable()
    {
        // Fail open toward retrying: a status this policy has never seen must not
        // silently become a terminal state that strands a card.
        var result = new GitPushResult(false, "abc123", "some-new-git-status", null);

        Assert.Equal(GitPushReaction.RetryLater, GitPushOutcomePolicy.Decide(result));
    }

    [Theory]
    [InlineData("LINEAGE-BLOCKED")]
    [InlineData("  lineage-blocked  ")]
    public void BlockedStatus_MatchIsCaseAndWhitespaceInsensitive(string status)
        => Assert.Equal(
            GitPushReaction.Blocked,
            GitPushOutcomePolicy.Decide(new GitPushResult(false, "abc123", status, null)));

    [Fact]
    public void NullStatus_DoesNotThrow()
        => Assert.Equal(
            GitPushReaction.RetryLater,
            GitPushOutcomePolicy.Decide(success: false, status: null));
}
