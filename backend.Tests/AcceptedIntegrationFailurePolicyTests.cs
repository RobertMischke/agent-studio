using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptedIntegrationFailurePolicyTests
{
    public static TheoryData<string, string, string, bool> FailureMatrix => new()
    {
        {
            "conflict",
            "Merge conflict in one file.",
            AcceptedIntegrationFailureCodes.MergeConflict,
            true
        },
        {
            "delivery-gate-failed",
            "The Remote delivery gate rejected the reviewed result.",
            AcceptedIntegrationFailureCodes.DeliveryGateFailed,
            false
        },
        {
            "gate-failed",
            "The build gate blocked the merge.",
            AcceptedIntegrationFailureCodes.BuildGateFailed,
            false
        },
        {
            "error",
            "Release source 'origin/task' must be rebased onto 'main' before the full-suite gate.",
            AcceptedIntegrationFailureCodes.SourceNeedsRebase,
            true
        },
        {
            "agent-round-required",
            "Mechanical rebase changed the delivery commit cardinality.",
            AcceptedIntegrationFailureCodes.DeliveryAttributionAmbiguous,
            false
        },
        {
            "error",
            "The accepted task has no stable key for review-subject validation.",
            AcceptedIntegrationFailureCodes.ReviewSubjectTaskKeyUnavailable,
            false
        },
        {
            "error",
            "Review subject RunAttempt 'old' is stale; current RunAttempt is 'new'.",
            AcceptedIntegrationFailureCodes.ReviewSubjectInvalid,
            false
        },
        {
            "error",
            "Could not synchronize the integration branch.",
            AcceptedIntegrationFailureCodes.IntegrationError,
            false
        },
        {
            "no-branch",
            "No task branch to merge.",
            AcceptedIntegrationFailureCodes.NoTaskBranch,
            false
        },
        {
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            "develop diverged from origin and automatic reconciliation failed.",
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            false
        },
        {
            "lineage-blocked",
            "Push of the integration branch to origin was rejected (remote-rejected).",
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            false
        },
    };

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public void Classify_MapsFailureToStableCardState(
        string verdict,
        string reason,
        string expectedCode,
        bool recoveryAvailable)
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            verdict == "no-branch" ? PipelineStepStatus.Skipped : PipelineStepStatus.Failed,
            verdict,
            reason,
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(recoveryAvailable, failure.RebaseRecoveryAvailable);
        Assert.False(string.IsNullOrWhiteSpace(failure.Label));
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }

    [Fact]
    public void Classify_PassedStep_HasNoFailure()
    {
        Assert.Null(AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Passed,
            "already-merged",
            "No merge needed.",
            verdictSummary: null));
    }

    /// <summary>
    /// AGT-2688: an integration-branch divergence must read as its own honest,
    /// non-recoverable-by-rebase state - never the generic "Integration
    /// failed" bucket a diverged-develop error used to fall into, and never
    /// eligible for the operator rebase-recovery action (rebasing THIS
    /// delivery's own branch cannot fast-forward a branch the platform itself
    /// cannot push).
    /// </summary>
    [Fact]
    public void Classify_IntegrationPushBlocked_IsDistinctFromGenericIntegrationError()
    {
        var pushBlocked = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            "develop diverged from origin - heal or recreate it via project settings before accepting deliveries. "
                + "Automatic reconciliation was attempted and hit a content conflict; a human must resolve it directly on 'develop'.",
            verdictSummary: null);

        Assert.NotNull(pushBlocked);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPushBlocked, pushBlocked!.Code);
        Assert.NotEqual(AcceptedIntegrationFailureCodes.IntegrationError, pushBlocked.Code);
        Assert.False(pushBlocked.RebaseRecoveryAvailable);
        Assert.Contains("develop diverged", pushBlocked.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
