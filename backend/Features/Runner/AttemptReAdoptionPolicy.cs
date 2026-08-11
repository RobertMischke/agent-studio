using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

internal enum AttemptReAdoptionDecision
{
    Adopt,
    RejectInvalidKind,
    RejectUnknownAttempt,
    RejectNotCurrent,
    RejectTerminal,
    RejectAuthorityMismatch,
}

internal sealed record AttemptReAdoptionFacts(
    string ReportedKind,
    bool AttemptExists,
    bool IsCurrent,
    bool IsLeased,
    bool HasLease,
    bool TaskMatches,
    bool ExecutorMatches,
    bool LeaseMatches,
    bool FenceMatches,
    bool EpochMatches,
    bool InstanceMatches);

/// <summary>
/// Pure fenced re-adoption decision. Lease expiry is intentionally absent: an
/// exact report may revive an expired lease only while it is still the current
/// generation. A higher-fence takeover changes the current/fence facts first
/// and therefore rejects the old report.
/// </summary>
internal static class AttemptReAdoptionPolicy
{
    public static AttemptReAdoptionDecision Decide(AttemptReAdoptionFacts facts)
    {
        if (facts.ReportedKind is not (Contract.RunnerAttemptKinds.Coding or Contract.RunnerAttemptKinds.Review))
            return AttemptReAdoptionDecision.RejectInvalidKind;
        if (!facts.AttemptExists)
            return AttemptReAdoptionDecision.RejectUnknownAttempt;
        if (!facts.IsCurrent)
            return AttemptReAdoptionDecision.RejectNotCurrent;
        if (!facts.IsLeased || !facts.HasLease)
            return AttemptReAdoptionDecision.RejectTerminal;
        return facts.TaskMatches
               && facts.ExecutorMatches
               && facts.LeaseMatches
               && facts.FenceMatches
               && facts.EpochMatches
               && facts.InstanceMatches
            ? AttemptReAdoptionDecision.Adopt
            : AttemptReAdoptionDecision.RejectAuthorityMismatch;
    }
}
