using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the completed-job auto-push outcome buckets. The case that
/// matters operationally is <c>lineage-blocked</c>: a dual-line repository
/// refuses a raw task commit on the release line by design, so it must classify
/// as <see cref="CompletedPushOutcome.TopologySkip"/> and never reach the
/// push-failure alarm channel.
/// </summary>
public sealed class CompletedPushOutcomePolicyTests
{
    [Theory]
    // Success statuses.
    [InlineData(true, "pushed", CompletedPushOutcome.Pushed)]
    [InlineData(true, "already-remote", CompletedPushOutcome.AlreadyPublished)]
    [InlineData(true, "no-remote", CompletedPushOutcome.AlreadyPublished)]
    // The topology refusal - intended, permanent, must not alarm.
    [InlineData(false, "lineage-blocked", CompletedPushOutcome.TopologySkip)]
    // Genuine faults an operator may need to act on.
    [InlineData(false, "remote-rejected", CompletedPushOutcome.Failed)]
    [InlineData(false, "failed", CompletedPushOutcome.Failed)]
    [InlineData(false, "lineage-check-failed", CompletedPushOutcome.Failed)]
    [InlineData(false, "missing-sha", CompletedPushOutcome.Failed)]
    [InlineData(false, "repo-missing", CompletedPushOutcome.Failed)]
    [InlineData(false, "invalid-branch", CompletedPushOutcome.Failed)]
    [InlineData(false, "cancelled", CompletedPushOutcome.Failed)]
    [InlineData(false, null, CompletedPushOutcome.Failed)]
    public void Classify_MapsPushStatusToOutcome(
        bool success,
        string? status,
        CompletedPushOutcome expected)
        => Assert.Equal(expected, CompletedPushOutcomePolicy.Classify(success, status));

    /// <summary>
    /// A failed push whose status merely contains the lineage token is still a
    /// fault; only the exact guard status is benign.
    /// </summary>
    [Fact]
    public void Classify_TreatsOnlyTheExactLineageStatusAsBenign()
    {
        Assert.Equal(
            CompletedPushOutcome.Failed,
            CompletedPushOutcomePolicy.Classify(false, "lineage-blocked-upstream"));
        Assert.Equal(
            CompletedPushOutcome.Failed,
            CompletedPushOutcomePolicy.Classify(false, "Lineage-Blocked"));
    }
}
