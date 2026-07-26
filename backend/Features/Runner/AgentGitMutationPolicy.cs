namespace AgentStudio.Runner;

/// <summary>
/// Post-run classification for worker-owned git mutations. A linear HEAD
/// advance is recoverable bookkeeping drift, not a failed run. Only verified
/// damage to shared history or a protected remote ref is terminal.
/// </summary>
internal static class AgentGitMutationPolicy
{
    internal static AgentGitMutationDecision Decide(
        string? headBefore,
        string? headAfter,
        bool headBeforeIsAncestorOfAfter,
        bool preExistingHistoryRewritten,
        bool protectedRemoteChanged,
        bool workerReportedPushOrCommit)
    {
        var headChanged = !string.IsNullOrWhiteSpace(headBefore)
            && !string.IsNullOrWhiteSpace(headAfter)
            && !string.Equals(headBefore, headAfter, StringComparison.OrdinalIgnoreCase);

        if (protectedRemoteChanged)
        {
            return new AgentGitMutationDecision(
                AgentGitMutationDisposition.Escalate,
                CleanupEligible: false,
                "worker push changed a protected remote branch");
        }

        if (preExistingHistoryRewritten)
        {
            return new AgentGitMutationDecision(
                AgentGitMutationDisposition.Escalate,
                CleanupEligible: false,
                "worker rewrote history that existed before this run");
        }

        if (headChanged)
        {
            return new AgentGitMutationDecision(
                AgentGitMutationDisposition.Info,
                CleanupEligible: headBeforeIsAncestorOfAfter,
                "worker advanced HEAD before the platform commit");
        }

        if (workerReportedPushOrCommit)
        {
            return new AgentGitMutationDecision(
                AgentGitMutationDisposition.Info,
                CleanupEligible: false,
                "worker output reported a git commit or push, but no protected-ref or HEAD damage was verified");
        }

        return new AgentGitMutationDecision(
            AgentGitMutationDisposition.None,
            CleanupEligible: false,
            "no worker git mutation detected");
    }
}

internal enum AgentGitMutationDisposition
{
    None,
    Info,
    Escalate,
}

internal sealed record AgentGitMutationDecision(
    AgentGitMutationDisposition Disposition,
    bool CleanupEligible,
    string Reason);
