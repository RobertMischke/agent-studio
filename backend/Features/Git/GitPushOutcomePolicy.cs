namespace AgentStudio.Git;

/// <summary>
/// What the platform must do next with one <see cref="GitPushResult"/>.
/// </summary>
public enum GitPushReaction
{
    /// <summary>The object is on origin (freshly pushed or already contained). Nothing left to do.</summary>
    Published,

    /// <summary>
    /// The push did not land for a reason that can clear on its own (network
    /// glitch, unavailable remote, a checkout that is momentarily missing, a
    /// remote that has since moved on). A later sweep may attempt it again.
    /// </summary>
    RetryLater,

    /// <summary>
    /// The push was refused for a STRUCTURAL reason: the repository topology or
    /// the request itself forbids it, and the exact same attempt is guaranteed to
    /// be refused again. Retrying is pure waste, so the caller must record the
    /// refusal as a visible terminal fact instead of sweeping it forever.
    /// </summary>
    Blocked,
}

/// <summary>
/// Pure classifier for the status vocabulary that <see cref="GitService.PushShaAsync"/>
/// and <see cref="GitService.PushIntegrationBranchAsync"/> return.
///
/// <para>
/// AGT-2688: the completed-job auto-push targets <c>main</c>, and in a repository
/// that also has a <c>develop</c> line the lineage guard correctly refuses every
/// raw task commit with <c>lineage-blocked</c>. That refusal is a permanent
/// property of the branch topology, not a blip - but the periodic backstop
/// re-scanned every completed job every 15 minutes and re-attempted every commit,
/// producing hundreds of identical "Auto-push skipped ... lineage-blocked"
/// warnings, an operator-feed event per sweep, and no terminal state anywhere.
/// Separating "can clear on its own" from "can never clear" is what stops that
/// loop, so the distinction lives here as one pure decision with a direct matrix
/// test rather than as an inline <c>switch</c> at each call site.
/// </para>
/// </summary>
public static class GitPushOutcomePolicy
{
    /// <summary>
    /// The refusal a dual-line repository raises when a raw task or delivery
    /// commit is offered directly to <c>main</c>. Callers surface it as its own
    /// state so it is never read as an ordinary "not integrated yet".
    /// </summary>
    public const string LineageBlockedStatus = "lineage-blocked";

    public static GitPushReaction Decide(GitPushResult result)
        => Decide(result.Success, result.Status);

    public static GitPushReaction Decide(bool success, string? status)
    {
        // A successful result is published regardless of which success flavour it
        // carried ("pushed", "already-remote", "no-remote" for a local-only
        // project). Trusting Success here keeps this policy correct if the git
        // layer ever adds another benign status.
        if (success) return GitPushReaction.Published;

        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            // Structural: the branch topology forbids this exact advance. Only a
            // different candidate (the published develop tip) could ever be
            // accepted, so the same commit will be refused on every future sweep.
            LineageBlockedStatus => GitPushReaction.Blocked,

            // Structural: a malformed ref/SHA, an object this repository does not
            // contain, or an approved SHA the branch never carried. None of these
            // become valid by waiting.
            "invalid-sha" or "invalid-branch" or "missing-sha" or "sha-not-on-branch"
                => GitPushReaction.Blocked,

            // Everything else is an environment or timing fault: the remote was
            // unreachable ("failed"), had moved on ("remote-rejected"), could not
            // be inspected ("lineage-check-failed"), the checkout was missing, or
            // the attempt was cancelled by shutdown. A later sweep is meaningful.
            _ => GitPushReaction.RetryLater,
        };
    }

    /// <summary>
    /// True when the refusal is permanent, so the caller must stop retrying and
    /// record it as a visible terminal fact.
    /// </summary>
    public static bool IsStructurallyBlocked(GitPushResult result)
        => Decide(result) == GitPushReaction.Blocked;
}
