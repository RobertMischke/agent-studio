namespace AgentStudio.Pipeline;

/// <summary>
/// Decides how the shared checkout's local integration branch must converge on
/// <c>origin/&lt;branch&gt;</c> before a delivery is merged into it.
///
/// <para>The integration line has two local writers (the delivery merge and the
/// gate rollback) but a single publisher. Whenever a local merge lands and its
/// push does not, the local branch is ahead; if origin then advances from any
/// other source the two lines diverge. A <c>--ff-only</c> synchronization can
/// never heal that divergence, so every later integration fails while the push
/// backstop keeps retrying a push that can only be rejected. This policy names
/// the one case that must be healed by merging origin back into the local
/// branch, which makes the next publish a fast-forward again.</para>
/// </summary>
public static class IntegrationBranchReconciliationPolicy
{
    public static IntegrationBranchReconciliationDecision Decide(
        bool hasRemote,
        bool localBranchExists,
        bool tipsEqual,
        bool remoteIsAncestorOfLocal,
        bool localIsAncestorOfRemote)
    {
        if (!hasRemote)
            return new(IntegrationBranchReconciliationMode.LocalOnly);
        if (!localBranchExists)
            return new(IntegrationBranchReconciliationMode.CreateFromRemote);
        if (tipsEqual)
            return new(IntegrationBranchReconciliationMode.AlreadyCurrent);
        if (localIsAncestorOfRemote)
            return new(IntegrationBranchReconciliationMode.FastForwardFromRemote);
        if (remoteIsAncestorOfLocal)
            return new(IntegrationBranchReconciliationMode.PublishLocal);

        return new(
            IntegrationBranchReconciliationMode.MergeRemoteIntoLocal,
            "The local integration branch and origin have diverged. Merging origin back into the "
            + "local branch is the only convergence that neither discards a published commit nor "
            + "rewrites local integration history; the next publish is then a fast-forward.");
    }
}

public sealed record IntegrationBranchReconciliationDecision(
    IntegrationBranchReconciliationMode Mode,
    string? Reason = null);

public enum IntegrationBranchReconciliationMode
{
    /// <summary>No <c>origin</c> remote: the local branch is the only line.</summary>
    LocalOnly,

    /// <summary>No local branch yet; create it at the published tip.</summary>
    CreateFromRemote,

    /// <summary>Both tips are the same commit.</summary>
    AlreadyCurrent,

    /// <summary>Origin is strictly ahead; fast-forward the local branch.</summary>
    FastForwardFromRemote,

    /// <summary>The local branch is strictly ahead; publishing it fast-forwards origin.</summary>
    PublishLocal,

    /// <summary>The lines diverged; merge origin into the local branch to converge.</summary>
    MergeRemoteIntoLocal,
}
