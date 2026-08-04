namespace AgentStudio.Tasks;

/// <summary>The authority action for a Remote coding completion's immutable result envelope.</summary>
public enum RemoteCompletionEnvelopeDisposition
{
    NotRequired,
    Persist,
    EscalateUnverified,
}

/// <summary>A pure completion-boundary decision made before attempt settlement.</summary>
public sealed record RemoteCompletionEnvelopeDecision(
    RemoteCompletionEnvelopeDisposition Disposition,
    string AuthorityOutcome,
    string? Reason = null)
{
    public bool ShouldPersist => Disposition == RemoteCompletionEnvelopeDisposition.Persist;
    public bool ShouldEscalate => Disposition == RemoteCompletionEnvelopeDisposition.EscalateUnverified;
}

/// <summary>
/// Prevents a successful coding RunAttempt from becoming reviewable unless the
/// server can persist the complete immutable result envelope used to materialize
/// its ReviewSubject.
/// </summary>
public static class RemoteCompletionEnvelopePolicy
{
    public static RemoteCompletionEnvelopeDecision Decide(
        bool requiresEnvelope,
        string? reportedOutcome,
        bool runAttemptKnown,
        bool hasBaseSha,
        bool hasImmutableResultRef,
        bool hasArtifactManifestDigest)
    {
        var outcome = reportedOutcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!requiresEnvelope || outcome is not ("done" or "noop"))
        {
            return new RemoteCompletionEnvelopeDecision(
                RemoteCompletionEnvelopeDisposition.NotRequired,
                outcome);
        }

        var missing = new List<string>(4);
        if (!runAttemptKnown) missing.Add("RunAttemptAuthority");
        if (!hasBaseSha) missing.Add("BaseSha");
        if (!hasImmutableResultRef) missing.Add("ImmutableResultRef");
        if (!hasArtifactManifestDigest) missing.Add("ArtifactManifestDigest");
        if (missing.Count == 0)
        {
            return new RemoteCompletionEnvelopeDecision(
                RemoteCompletionEnvelopeDisposition.Persist,
                outcome);
        }

        return new RemoteCompletionEnvelopeDecision(
            RemoteCompletionEnvelopeDisposition.EscalateUnverified,
            "unverified",
            "Remote coding completion reported success without a complete immutable result envelope "
            + $"(missing: {string.Join(", ", missing)}). The delivery is unverified and no ReviewSubject "
            + "was created. Requeue the card on a runner that can publish and persist the immutable result handoff.");
    }
}
