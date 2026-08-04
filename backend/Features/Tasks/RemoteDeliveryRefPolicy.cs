using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>Which reported ref a completion's delivery claim was taken from.</summary>
public enum RemoteDeliveryRefOrigin
{
    /// <summary>No ref was reported at all.</summary>
    None,

    /// <summary>The fenced immutable result ref of the run's result envelope.</summary>
    ImmutableResult,

    /// <summary>
    /// The collision branch a divergent salvage published the run's own result
    /// to, while the canonical branch stayed on the remote tip.
    /// </summary>
    SalvageRecovery,

    /// <summary>The canonical salvage branch of the run.</summary>
    SalvageBranch,
}

/// <summary>One reported ref that may carry the completion's fenced result SHA.</summary>
public sealed record RemoteDeliveryRefCandidate(
    string Ref,
    RemoteDeliveryRefOrigin Origin);

/// <summary>
/// The ref a completion claims as its review subject, together with what the
/// target repository said about that claim.
/// </summary>
public sealed record RemoteDeliveryRefSelection(
    string? Ref,
    RemoteDeliveryRefOrigin Origin,
    DeliveryVerificationStatus Verification)
{
    public static RemoteDeliveryRefSelection None { get; } = new(
        null,
        RemoteDeliveryRefOrigin.None,
        DeliveryVerificationStatus.NotVerifiable);

    /// <summary>
    /// True when the repository positively confirmed that this ref carries the
    /// claimed result SHA - as its tip or inside its history
    /// (<see cref="DeliveryVerificationStatus.VerifiedContained"/>).
    /// </summary>
    public bool CarriesResult =>
        Verification is DeliveryVerificationStatus.Verified
            or DeliveryVerificationStatus.VerifiedContained;
}

/// <summary>
/// AGT-2494 - picks the ref a remote completion may name as its review subject.
///
/// <para>A divergent salvage resolution splits the run in two: the canonical
/// branch keeps the remote tip it collided with, and the run's own result is
/// published to a <c>...-collision-&lt;sha&gt;-&lt;sha&gt;</c> recovery branch.
/// Reporting the canonical branch next to that result SHA produces a review
/// subject that cannot resolve by construction - the exact shape that made
/// AGT-2220 fail with <c>immutable-result-mismatch</c> and an empty
/// <c>commits[]</c> on 28.07.</para>
///
/// <para>The policy is pure: it ranks the reported refs by how likely they are
/// to carry the result. <see cref="Select"/> then lets the target repository
/// decide, so the completion never claims a ref that provably does not hold the
/// SHA while another reported ref does.</para>
/// </summary>
public static class RemoteDeliveryRefPolicy
{
    public const string DivergentResolution = "divergent";

    /// <summary>
    /// Ranks the reported refs, best claim first. The immutable result ref wins
    /// when present - it is the reviewed delivery, and a salvage branch is
    /// recovery evidence only. A divergent recovery branch outranks the
    /// canonical salvage branch because the reconciliation itself recorded that
    /// the canonical branch holds a different commit.
    /// </summary>
    public static IReadOnlyList<RemoteDeliveryRefCandidate> Candidates(
        string? immutableResultRef,
        string? salvageResolution,
        string? salvageBranch,
        string? salvageRecoveryBranch,
        string? salvageRecoveryCommitSha,
        string? resultSha)
    {
        var candidates = new List<RemoteDeliveryRefCandidate>(3);
        Add(immutableResultRef, RemoteDeliveryRefOrigin.ImmutableResult);
        if (IsDivergent(salvageResolution)
            && CarriesReportedResult(salvageRecoveryCommitSha, resultSha))
        {
            Add(salvageRecoveryBranch, RemoteDeliveryRefOrigin.SalvageRecovery);
        }
        Add(salvageBranch, RemoteDeliveryRefOrigin.SalvageBranch);
        return candidates;

        void Add(string? gitRef, RemoteDeliveryRefOrigin origin)
        {
            var trimmed = gitRef?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            if (candidates.Any(candidate =>
                    string.Equals(candidate.Ref, trimmed, StringComparison.Ordinal)))
            {
                return;
            }
            candidates.Add(new RemoteDeliveryRefCandidate(trimmed, origin));
        }
    }

    /// <summary>
    /// Walks the ranked candidates and returns the first one the repository
    /// confirms carries the result SHA. A candidate the repository actively
    /// contradicts is skipped in favour of the next one; a candidate that merely
    /// cannot be checked ends the walk and keeps its rank, because "could not
    /// look" is never disproof. When every candidate is disproved the best-ranked
    /// claim is returned unchanged so the existing AGT-2220 disproof gate can
    /// escalate it honestly instead of the card being stamped Done.
    /// </summary>
    public static RemoteDeliveryRefSelection Select(
        IReadOnlyList<RemoteDeliveryRefCandidate> candidates,
        Func<string, DeliveryVerificationResult> verify)
    {
        RemoteDeliveryRefSelection? best = null;
        foreach (var candidate in candidates)
        {
            var verification = verify(candidate.Ref);
            var selection = new RemoteDeliveryRefSelection(
                candidate.Ref, candidate.Origin, verification.Status);
            if (selection.CarriesResult) return selection;
            best ??= selection;
            if (!verification.IsDisproved) return selection;
        }
        return best ?? RemoteDeliveryRefSelection.None;
    }

    private static bool IsDivergent(string? salvageResolution) =>
        string.Equals(
            salvageResolution?.Trim(),
            DivergentResolution,
            StringComparison.OrdinalIgnoreCase);

    private static bool CarriesReportedResult(string? recoveryCommitSha, string? resultSha) =>
        !string.IsNullOrWhiteSpace(recoveryCommitSha)
        && !string.IsNullOrWhiteSpace(resultSha)
        && string.Equals(
            recoveryCommitSha!.Trim(),
            resultSha!.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
