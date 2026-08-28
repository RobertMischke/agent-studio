namespace AgentStudio.Runner;

/// <summary>
/// Classifies the result of the completed-job auto-push so that a refusal the
/// branch topology mandates is never reported as a push failure.
///
/// <para>
/// The auto-push publishes a platform-owned task commit by advancing the
/// release line. In a repository that also carries a work line, that is exactly
/// what <c>ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance</c> forbids:
/// a raw task commit may not advance <c>main</c>, because the delivery reaches
/// the release line only through develop integration. The resulting
/// <c>lineage-blocked</c> status is therefore a permanent, intended property of
/// the project's topology, not an incident.
/// </para>
///
/// <para>
/// Treating it as a failure is what made it harmful: the completed-push backstop
/// re-sweeps every completed card on a fixed interval, so a single dual-line
/// project emitted one warning plus one <c>managed-repo-push-failed</c> event per
/// completed card per sweep, indefinitely. That drowned the real push failures
/// (<c>remote-rejected</c>, <c>failed</c>) that an operator must act on.
/// <see cref="CompletedPushOutcome.TopologySkip"/> keeps the outcome visible in
/// the log while leaving the alarm channel for faults.
/// </para>
/// </summary>
public static class CompletedPushOutcomePolicy
{
    /// <summary>The lineage guard's status for a refused direct release-line advance.</summary>
    public const string LineageBlockedStatus = "lineage-blocked";

    /// <summary>The status returned when the push advanced the target branch.</summary>
    public const string PushedStatus = "pushed";

    public static CompletedPushOutcome Classify(bool success, string? status)
    {
        if (success)
        {
            return string.Equals(status, PushedStatus, StringComparison.Ordinal)
                ? CompletedPushOutcome.Pushed
                : CompletedPushOutcome.AlreadyPublished;
        }

        return string.Equals(status, LineageBlockedStatus, StringComparison.Ordinal)
            ? CompletedPushOutcome.TopologySkip
            : CompletedPushOutcome.Failed;
    }
}

public enum CompletedPushOutcome
{
    /// <summary>The push advanced the target branch; count it as published work.</summary>
    Pushed,

    /// <summary>Benign success: the commit was already on the remote, or there is no remote.</summary>
    AlreadyPublished,

    /// <summary>
    /// The branch topology forbids this push by design. Log it, do not alarm,
    /// and do not count it as published work.
    /// </summary>
    TopologySkip,

    /// <summary>A genuine push fault an operator may need to act on.</summary>
    Failed,
}
