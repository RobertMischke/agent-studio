namespace AgentStudio.Runner;

public enum ProviderLimitRunDisposition
{
    ContinueOutcomePolicy,
    WaitWithoutTaskFailure,
}

/// <summary>
/// Separates account capability exhaustion from task-owned outcomes before
/// escalation and breaker accounting can observe the run.
/// </summary>
public static class ProviderLimitRunPolicy
{
    public static ProviderLimitRunDisposition Decide(
        RunIssueKind issueKind,
        string? cliType) =>
        issueKind == RunIssueKind.QuotaExhausted && !string.IsNullOrWhiteSpace(cliType)
            ? ProviderLimitRunDisposition.WaitWithoutTaskFailure
            : ProviderLimitRunDisposition.ContinueOutcomePolicy;
}
