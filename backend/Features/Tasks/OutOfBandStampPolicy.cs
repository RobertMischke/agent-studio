using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>What an out-of-band completion claim is allowed to do to a card.</summary>
public enum OutOfBandStampDecision
{
    /// <summary>The delivery was proven against the target repository - stamp it.</summary>
    Stamp,

    /// <summary>
    /// No delivery was claimed at all, and the target lane is not terminal. The
    /// card is still reconciled - refusing here would resurrect the abandoned
    /// "escalated / no summary" corpse this endpoint exists to retire (AGT-1917),
    /// and it would break the worktree-blocked escalation, whose whole point is
    /// that nothing could be secured. But the card is marked
    /// <c>delivery:unverified</c> and says so, and it gets no <c>commits[]</c>:
    /// reconciled is not the same as delivered.
    /// </summary>
    StampUnproven,

    /// <summary>
    /// Either the repository actively contradicts the claim, or a terminal lane
    /// was requested without proof. The card gets an honest
    /// <c>unverified-delivery</c> state instead of a completion stamp.
    /// </summary>
    RefuseUnverified,
}

/// <summary>
/// AGT-2220 - the invariant, as one pure decision function: <em>an out-of-band /
/// external completion stamp requires commits that provably exist in the target
/// repository.</em>
///
/// <para>History this closes: the ghost badges of AGT-2400, the 11.07. phantom
/// wave (a whole remote wave stamped "completed" while nothing had been pushed),
/// and the "Delivered lies" conflict-skipped series. All three shared one shape:
/// a terminal stamp written from <em>prose</em> ("the repository was verified
/// at &lt;ref&gt;") that nothing ever re-checked against git.</para>
///
/// <para>The rule deliberately fails closed. "We could not look" is not proof,
/// so it refuses just like an outright mismatch - only the reason differs. Cards
/// that produce no commits at all (report-only planning/research modes) are the
/// single exemption, because for them there is no repository claim to verify.</para>
/// </summary>
public static class OutOfBandStampPolicy
{
    /// <summary>Board-visible marker for a refused, unproven delivery.</summary>
    public const string UnverifiedDeliveryTag = "delivery:unverified";

    /// <summary>Board-visible marker for a stamp that carries repository proof.</summary>
    public const string VerifiedDeliveryTag = "delivery:verified";

    /// <summary>
    /// Report-only modes deliver a document into the task folder, not commits
    /// into the project repository, so they carry no repository claim to prove.
    /// Everything else (coding, concept) must prove its delivery.
    /// </summary>
    public static bool RequiresRepositoryProof(string? mode) => !TaskModes.IsReportOnly(mode);

    /// <summary>
    /// Lanes from which a card reads as finished. Reaching one of these
    /// out-of-band is what the 11.07. phantom wave did, so they are the lanes
    /// that require repository proof.
    /// </summary>
    public static bool IsTerminalLane(string? targetState) =>
        string.Equals(targetState, TaskStates.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(targetState, TaskStates.Archive, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides what a claimed out-of-band completion may do to the card.
    ///
    /// <para>The rule is deliberately asymmetric, because the two failure shapes
    /// in the history are different. A claim the repository <em>contradicts</em>
    /// ("Delivered lies") is always refused - a false claim is worse than none.
    /// <em>No</em> claim is not a lie, so it still reconciles the card into a
    /// non-terminal lane (an operator rescuing a stuck card rarely has a SHA at
    /// hand, and the worktree-blocked escalation by definition has none) - but it
    /// never reaches a terminal lane and never writes <c>commits[]</c>.</para>
    ///
    /// <para><paramref name="verification"/> is <c>null</c> when the request
    /// carried no commit claim at all.</para>
    /// </summary>
    public static (OutOfBandStampDecision Decision, string Reason) Decide(
        string? mode,
        string? targetState,
        DeliveryVerificationResult? verification)
    {
        // A claim the repository contradicts is refused everywhere - there is no
        // lane in which a false delivery claim is acceptable.
        if (verification is not null && verification.IsDisproved)
            return (OutOfBandStampDecision.RefuseUnverified, verification.Note);

        if (!RequiresRepositoryProof(mode))
        {
            return (OutOfBandStampDecision.Stamp,
                $"Mode '{TaskModes.Normalize(mode)}' liefert keine Commits ins Zielrepo; "
                + "es gibt keinen Repository-Anspruch zu pruefen.");
        }

        if (verification is { IsVerified: true })
            return (OutOfBandStampDecision.Stamp, verification.Note);

        // From here on: no claim, or a claim that could not be checked.
        var why = verification is null
            ? "Kein Commit-Anspruch mitgeliefert."
            : verification.Note;

        if (IsTerminalLane(targetState))
        {
            return (OutOfBandStampDecision.RefuseUnverified,
                why + " Eine Coding-Karte ohne verifizierbaren Commit im Zielrepo darf nicht "
                    + $"terminal nach '{targetState}' gestempelt werden (Phantom-Muster 11.07.).");
        }

        return (OutOfBandStampDecision.StampUnproven,
            why + " Die Karte wird versorgt, aber als unbestaetigte Lieferung gefuehrt: "
                + "kein commits[]-Eintrag, kein terminaler Stempel.");
    }
}
