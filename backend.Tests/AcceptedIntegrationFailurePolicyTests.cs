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
}
