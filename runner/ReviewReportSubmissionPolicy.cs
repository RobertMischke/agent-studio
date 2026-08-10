namespace AgentRunner;

public enum ReviewReportSubmissionAction
{
    Retry,
    TerminalTaskMissing,
    TerminalSuperseded,
    TerminalRejected,
}

/// <summary>
/// Pure classification for a failed terminal review-report delivery. Transport
/// and ordinary 5xx failures are retried. Missing task authority and fenced 4xx
/// replies are terminal because replay cannot make the same identity current.
/// </summary>
public static class ReviewReportSubmissionPolicy
{
    public static ReviewReportSubmissionAction Decide(
        int? statusCode,
        string? errorCode,
        bool transportFailure)
    {
        if (string.Equals(errorCode, "task-not-found", StringComparison.OrdinalIgnoreCase)
            || statusCode == 404)
        {
            return ReviewReportSubmissionAction.TerminalTaskMissing;
        }

        if (string.Equals(errorCode, "Superseded", StringComparison.OrdinalIgnoreCase))
            return ReviewReportSubmissionAction.TerminalSuperseded;

        if (transportFailure
            || statusCode is null
            || statusCode is 408 or 429
            || statusCode >= 500)
            return ReviewReportSubmissionAction.Retry;

        return ReviewReportSubmissionAction.TerminalRejected;
    }

    public static string TerminalClassification(ReviewReportSubmissionAction action) => action switch
    {
        ReviewReportSubmissionAction.TerminalTaskMissing => "TaskNotFound",
        ReviewReportSubmissionAction.TerminalSuperseded => "Superseded",
        _ => "ReportRejected",
    };
}
