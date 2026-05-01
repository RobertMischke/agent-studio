namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pure finish-state policy for a CLI execution. Keeping this separate makes
/// the application-owned lifecycle explicit and easy to test.
/// </summary>
public static class RunCompletionPolicy
{
    public static bool ShouldMoveToReview(string? executionStatus) =>
        string.Equals(executionStatus, "completed", StringComparison.OrdinalIgnoreCase);
}
