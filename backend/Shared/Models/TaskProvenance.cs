namespace OrchestratorApi.Models;

/// <summary>
/// Append-only commit-provenance record for one task (ASS-1724, epic ASS-1720).
/// Anchored on the per-task worktree branch <c>task/&lt;id&gt;</c> so the board can
/// answer "where does this work live" graph-based across the
/// worktree -&gt; develop -&gt; main path, instead of guessing from a wall-clock
/// window.
///
/// <para>
/// Persisted as the <c>"provenance"</c> object in <c>task.json</c>. Only the
/// recording-relevant facts are stored here: the <see cref="Branch"/>, the
/// <see cref="Base"/> merge-base the branch was cut from, the per-lane-transition
/// <see cref="Transitions"/> anchors, and the <see cref="Merge"/> block (written
/// by sibling slice ASS-1721, not this one). The derived <c>landedState</c> is
/// NOT stored: graph ancestry queries are cheap to run but can go stale the
/// moment develop/main move, so the read endpoint recomputes it live (see
/// <see cref="TaskProvenanceView"/>).
/// </para>
/// </summary>
public record TaskProvenance
{
    /// <summary>The task's worktree branch, <c>task/&lt;sanitized-id&gt;</c>.</summary>
    public string Branch { get; init; } = "";

    /// <summary>
    /// Merge-base SHA the branch was cut from (its fork point off the integration
    /// branch). Captured once, on the first recorded transition that can see the
    /// branch; null for sequential runs that never cut a <c>task/&lt;id&gt;</c>
    /// branch.
    /// </summary>
    public string? Base { get; init; }

    /// <summary>
    /// One anchor per lane transition (oldest -&gt; newest). Each entry pins the
    /// branch tip and the integration-branch head at the instant the task entered
    /// the lane, so the ladder can be reconstructed historically.
    /// </summary>
    public List<TaskProvenanceTransition> Transitions { get; init; } = [];

    /// <summary>
    /// The develop merge record. Written by the "Merge into Develop" post-step
    /// (sibling slice ASS-1721); this slice only defines the shape and reads it
    /// through. Null until that step runs.
    /// </summary>
    public TaskProvenanceMerge? Merge { get; init; }
}

/// <summary>
/// A single lane-transition anchor inside <see cref="TaskProvenance.Transitions"/>.
/// Written by the ONE recording hook in <c>TaskTransitionService.MoveAsync</c>;
/// no other code path appends transitions.
/// </summary>
public record TaskProvenanceTransition
{
    /// <summary>The lane the task moved into (a <see cref="TaskStates"/> value).</summary>
    public string Lane { get; init; } = "";

    /// <summary>Wall-clock UTC instant of the transition.</summary>
    public DateTime AtUtc { get; init; }

    /// <summary>
    /// Tip SHA of <c>task/&lt;id&gt;</c> at the transition, or null when the task has
    /// no worktree branch (sequential run in the shared checkout).
    /// </summary>
    public string? BranchTip { get; init; }

    /// <summary>
    /// Head SHA of the integration branch (develop) at the transition. Lets the
    /// reader see how far develop had advanced when the task crossed each lane.
    /// </summary>
    public string? WorkBranchHead { get; init; }
}

/// <summary>
/// The develop merge record. Populated by sibling slice ASS-1721; defined here so
/// the wire shape is stable and the read path can surface it once it lands.
/// </summary>
public record TaskProvenanceMerge
{
    /// <summary>The <c>--no-ff</c> merge commit SHA on develop, when present.</summary>
    public string? MergeCommit { get; init; }

    /// <summary>Develop head SHA immediately before the merge.</summary>
    public string? WorkBranchHeadBefore { get; init; }

    /// <summary>Develop head SHA immediately after the merge (== the merge commit).</summary>
    public string? WorkBranchHeadAfter { get; init; }

    /// <summary>Wall-clock UTC instant of the merge.</summary>
    public DateTime AtUtc { get; init; }
}

/// <summary>
/// String constants for the derived landed-state. Kept as constants (not an enum)
/// so the wire format stays a literal string, consistent with
/// <see cref="CommitAttributionKinds"/> and <see cref="TaskTypes"/>.
/// </summary>
public static class LandedStates
{
    /// <summary>Work exists only on <c>task/&lt;id&gt;</c>; not yet folded into develop.</summary>
    public const string OnBranchOnly = "on-branch-only";

    /// <summary>The branch tip is an ancestor of develop (folded into integration).</summary>
    public const string MergedToDevelop = "merged-to-develop";

    /// <summary>The branch tip is an ancestor of main (shipped on the release line).</summary>
    public const string ReleasedToMain = "released-to-main";

    public static readonly string[] All = [OnBranchOnly, MergedToDevelop, ReleasedToMain];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OnBranchOnly;
        var v = value.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return s;
        return OnBranchOnly;
    }
}

/// <summary>
/// Read-time projection returned by <c>GET /api/tasks/{id}/provenance</c>. Carries
/// the persisted <see cref="TaskProvenance"/> facts plus everything derived live
/// from the graph: the <see cref="LandedState"/>, the <see cref="Ladder"/>
/// (task -&gt; develop -&gt; main with "HEAD now" SHAs), and per-commit
/// <see cref="Commits"/> membership. Never persisted.
/// </summary>
public record TaskProvenanceView
{
    public string Branch { get; init; } = "";
    public string? Base { get; init; }
    public List<TaskProvenanceTransition> Transitions { get; init; } = [];
    public TaskProvenanceMerge? Merge { get; init; }

    /// <summary>Derived landed-state, one of <see cref="LandedStates"/>.</summary>
    public string LandedState { get; init; } = LandedStates.OnBranchOnly;

    public TaskLandedLadder Ladder { get; init; } = new();

    /// <summary>
    /// Per-commit branch membership for the task's merge-set (graph: commits that
    /// <c>task/&lt;id&gt;</c> is ahead of <see cref="Base"/>, falling back to the
    /// persisted attributed chain when no branch exists).
    /// </summary>
    public List<TaskCommitMembership> Commits { get; init; } = [];
}

/// <summary>
/// The landed-ladder rungs: <c>task/&lt;id&gt;</c> -&gt; develop @sha -&gt; main @sha,
/// each with the live "HEAD now" SHA and a boolean for whether the task's work has
/// reached that rung.
/// </summary>
public record TaskLandedLadder
{
    public string Branch { get; init; } = "";
    public string? BranchTip { get; init; }

    public string IntegrationBranch { get; init; } = "develop";
    public string? IntegrationHead { get; init; }
    public bool MergedToIntegration { get; init; }

    public string ReleaseBranch { get; init; } = "main";
    public string? ReleaseHead { get; init; }
    public bool ReleasedToRelease { get; init; }
}

/// <summary>
/// One commit in the task's merge-set with its branch membership: always on the
/// task branch, optionally also reachable from develop / main.
/// </summary>
public record TaskCommitMembership
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Message { get; init; } = "";
    public bool OnTaskBranch { get; init; } = true;
    public bool AlsoOnIntegration { get; init; }
    public bool AlsoOnRelease { get; init; }
}
