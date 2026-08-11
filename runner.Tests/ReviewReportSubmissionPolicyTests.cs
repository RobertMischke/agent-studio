using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewReportSubmissionPolicyTests
{
    [Theory]
    [InlineData(null, null, true, ReviewReportSubmissionAction.Retry)]
    [InlineData(null, null, false, ReviewReportSubmissionAction.Retry)]
    [InlineData(408, "request-timeout", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(429, "rate-limited", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(500, null, false, ReviewReportSubmissionAction.Retry)]
    [InlineData(503, "temporary-overload", false, ReviewReportSubmissionAction.Retry)]
    [InlineData(503, "task-not-found", false, ReviewReportSubmissionAction.TerminalTaskMissing)]
    [InlineData(404, "not-found", false, ReviewReportSubmissionAction.TerminalTaskMissing)]
    [InlineData(409, "Superseded", false, ReviewReportSubmissionAction.TerminalSuperseded)]
    [InlineData(409, "stale-review-authority", false, ReviewReportSubmissionAction.TerminalRejected)]
    public void Decide_classifies_retryable_and_terminal_failures(
        int? statusCode,
        string? errorCode,
        bool transportFailure,
        ReviewReportSubmissionAction expected)
        => Assert.Equal(
            expected,
            ReviewReportSubmissionPolicy.Decide(statusCode, errorCode, transportFailure));
}
