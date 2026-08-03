using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>What an out-of-band completion claim is allowed to do to a card.</summary>
public enum OutOfBandStampDecision
{
    /// <summary>The delivery was proven against the target repository - stamp it.</summary>
    Stamp,

    /// <summary>
    /// No proof (missing, unverifiable, or actively contradicted). The card gets
    /// an honest <c>unverified-delivery</c> state instead of a completion stamp.
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
    /// Decides whether a claimed out-of-band completion may stamp the card.
    /// <paramref name="verification"/> is <c>null</c> when the request carried no
    /// commit claim at all - which for a proof-requiring mode is exactly the
    /// 11.07. phantom shape and therefore refused.
    /// </summary>
    public static (OutOfBandStampDecision Decision, string Reason) Decide(
        string? mode,
        DeliveryVerificationResult? verification)
    {
        if (!RequiresRepositoryProof(mode))
        {
            return (OutOfBandStampDecision.Stamp,
                $"Mode '{TaskModes.Normalize(mode)}' liefert keine Commits ins Zielrepo; "
                + "es gibt keinen Repository-Anspruch zu pruefen.");
        }

        if (verification is null)
        {
            return (OutOfBandStampDecision.RefuseUnverified,
                "Kein Commit-Anspruch mitgeliefert: eine Coding-Karte kann ohne verifizierbaren "
                + "Commit im Zielrepo nicht terminal gestempelt werden (Phantom-Muster 11.07.).");
        }

        return verification.IsVerified
            ? (OutOfBandStampDecision.Stamp, verification.Note)
            : (OutOfBandStampDecision.RefuseUnverified, verification.Note);
    }
}
