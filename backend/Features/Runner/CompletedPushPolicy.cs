namespace AgentStudio.Runner;

/// <summary>
/// How the platform must treat one completed-job auto-push attempt.
/// </summary>
public enum CompletedPushDisposition
{
    /// <summary>The commit is on origin, or there is no origin to publish to.</summary>
    Published,

    /// <summary>An environmental failure; the identical attempt can still succeed later.</summary>
    Retry,

    /// <summary>
    /// A lineage or policy refusal. The inputs are immutable (a fixed SHA against
    /// a fixed branch), so replaying the identical attempt can never succeed.
    /// </summary>
    Blocked,
}

/// <summary>
/// Pure classification of a <see cref="AgentStudio.Git.GitPushResult.Status"/>
/// produced by the completed-job auto-push.
///
/// <para>Auto-push used to treat every non-success identically: log a warning and
/// let the 15-minute backstop replay it. That is correct only for environmental
/// failures. A <c>lineage-blocked</c> or <c>remote-rejected</c> verdict is a
/// decision about immutable inputs - this exact SHA may not advance this exact
/// branch - so replaying it produces the same refusal forever. That loop was the
/// AGT-2688 failure mode: one refused card emitted the same warning every sweep,
/// burying the board's real state under hundreds of identical lines while the
/// commit silently never reached origin.</para>
///
/// <para>Splitting the verdict lets the caller retry what can still land and
/// alarm once on what cannot, instead of doing neither well.</para>
/// </summary>
public static class CompletedPushPolicy
{
    /// <summary>
    /// The work line a repository publishes platform-owned commits onto.
    /// </summary>
    public const string WorkBranch = "develop";

    /// <summary>
    /// Chooses the branch a completed job's commits are published to, given the
    /// branch the repository resolved for this project.
    ///
    /// <para>A dual-line repository has exactly one writer of the release line:
    /// the integration path, which advances <c>main</c> only from the published
    /// <c>develop</c> tip (<see cref="AgentStudio.Pipeline.ImmediateIntegrationLineagePolicy"/>).
    /// A raw platform commit aimed at <c>main</c> is therefore refused by the
    /// lineage guard by construction. Sending it to the work line instead is
    /// what makes both writers converge on one lineage, and it is a plain
    /// fast-forward because the integration path keeps local <c>develop</c>
    /// synchronized with origin before every merge.</para>
    ///
    /// <para>A repository with only a release line has no second lineage to
    /// converge on and keeps publishing to that line unchanged.</para>
    /// </summary>
    public static string ResolveTargetBranch(string? resolvedBranch, bool developLineExists)
    {
        var branch = (resolvedBranch ?? string.Empty).Trim();
        if (branch.Length == 0) return developLineExists ? WorkBranch : "main";

        var isReleaseLine = string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase);
        return isReleaseLine && developLineExists ? WorkBranch : branch;
    }

    public static CompletedPushDisposition Classify(bool success, string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (success)
            return CompletedPushDisposition.Published;

        return normalized switch
        {
            // The candidate may not advance the target line at all: a raw task
            // commit aimed at a release branch that only accepts the published
            // develop tip, or an object the branch does not contain.
            "lineage-blocked" => CompletedPushDisposition.Blocked,
            "sha-not-on-branch" => CompletedPushDisposition.Blocked,

            // Non-fast-forward. The remote moved on and this SHA can no longer
            // advance it; the delivery has to be re-integrated, not re-pushed.
            "remote-rejected" => CompletedPushDisposition.Blocked,

            // Malformed or unresolvable inputs; a sweep cannot repair them.
            "invalid-sha" => CompletedPushDisposition.Blocked,
            "invalid-branch" => CompletedPushDisposition.Blocked,
            "missing-sha" => CompletedPushDisposition.Blocked,

            // Everything else (network blips, a repo root that is not mounted
            // yet, a cancelled shutdown push) is environmental and retryable.
            _ => CompletedPushDisposition.Retry,
        };
    }
}

/// <summary>
/// One completed-push attempt that was refused on lineage or policy grounds,
/// carrying the branch it was actually aimed at so the operator feed never has
/// to guess which line refused the commit.
/// </summary>
public sealed record CompletedPushRefusal(
    string Sha,
    string TargetBranch,
    string Status,
    string? Error);

/// <summary>
/// The result of pushing one job's commits: how many reached origin, and which
/// attempts were refused for good. A refused attempt is deliberately not
/// counted as pushed and deliberately not reported as a transient failure.
/// </summary>
public sealed record CompletedPushOutcome(
    int Pushed,
    IReadOnlyList<CompletedPushRefusal> Refusals)
{
    public static readonly CompletedPushOutcome None = new(0, []);
}
