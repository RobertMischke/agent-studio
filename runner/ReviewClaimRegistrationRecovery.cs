namespace AgentRunner;

/// <summary>
/// Identifies claim failures that mean this daemon identity has lost review
/// authority and must perform its full registration again before polling.
/// </summary>
internal static class ReviewClaimRegistrationRecovery
{
    public static bool IsRequired(TaskServerException exception)
        => exception.StatusCode == 409
           && (string.Equals(exception.ErrorCode, "LeaseExpired", StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   exception.ErrorCode,
                   "review-executor-not-registered",
                   StringComparison.OrdinalIgnoreCase));
}
