namespace AgentStudio.Shared;

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
/// Compact, always-on board-card merge signal (AGT-2046): does this task's work
/// live in the integration branch (develop) and/or the release branch (main)?
///
/// <para>
/// Computed batched + cached per repository by <c>BoardMergeStatusService</c>
/// (O(repos) git spawns, NOT per card) and folded onto the board payload via
/// <c>TaskInfo.MergeSignal</c>, so the kanban card can render a two-segment
/// <c>[develop|main]</c> indicator without the per-task graph query the detail
/// header pays (<see cref="TaskProvenanceView"/>). Uses the same
/// worktree -&gt; develop -&gt; main semantics as the detail landed-state
/// (ASS-1724): "in develop" == the task's anchor commit is an ancestor of
/// develop; "in main" == an ancestor of main. Never persisted.
/// </para>
/// </summary>
public record TaskMergeSignal
{
    /// <summary>The task's worktree branch name, for the card's branch chip + tooltip.</summary>
    public string Branch { get; init; } = "";

    /// <summary>True when the task's work is folded into the integration branch (develop).</summary>
    public bool InIntegration { get; init; }

    /// <summary>True when the task's work has reached the release branch (main).</summary>
    public bool InRelease { get; init; }

    /// <summary>Integration branch name the signal was computed against (usually "develop").</summary>
    public string IntegrationBranch { get; init; } = "develop";

    /// <summary>Release branch name the signal was computed against (usually "main").</summary>
    public string ReleaseBranch { get; init; } = "main";

    /// <summary>
    /// Short SHA that proves the develop membership - the recorded develop-merge
    /// commit when present, else the contained anchor. Null when not in develop.
    /// </summary>
    public string? IntegrationSha { get; init; }

    /// <summary>Short SHA of the task anchor that reached main. Null when not in main.</summary>
    public string? ReleaseSha { get; init; }
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

/// <summary>
/// AGT-2202 — the honest, git-derived integration verdict for an <b>accepted</b>
/// card (5-human-review / 6-completed / 7-archive): is this task's work actually
/// folded into the integration branch (develop)?
///
/// <para>
/// Motivated by the 20.07. accept-run finding "Accept != Merge": 29 cards were
/// accepted while their code still sat un-merged on <c>task/</c> branches, because
/// the async curated auto-integrator merges out-of-band (a
/// <c>merge(&lt;KEY&gt;)</c> / <c>merge-recut(&lt;KEY&gt;)</c> commit on develop) and
/// the ephemeral <see cref="TaskMergeSignal"/> anchor-ancestry reading cannot see a
/// curated (rewritten) merge. Ground truth is only the develop git-log. This field
/// resolves the truth from three independent git signals and collapses it into one
/// of four discrete states, so the board can show a single, unambiguous "integrated
/// / not integrated / conflict / no branch" verdict on every accepted card and the
/// accept flow can flag an accept-without-merge the moment it happens.
/// </para>
///
/// <para>
/// Computed batched + cached per repository by
/// <c>TaskIntegrationStatusService</c> (O(repos) git spawns, never per card) and
/// folded onto the board payload; never persisted to <c>task.json</c>. Null on
/// cards that are not in an accepted lane.
/// </para>
/// </summary>
public record TaskIntegrationStatus
{
    /// <summary>One of <see cref="IntegrationStatuses"/>.</summary>
    public string Status { get; init; } = IntegrationStatuses.NoBranch;

    /// <summary>
    /// Short SHA that proves the integration when <see cref="Status"/> is
    /// <see cref="IntegrationStatuses.Integrated"/> - the curated
    /// <c>merge(&lt;KEY&gt;)</c> commit when present, else the contained task
    /// anchor / branch tip. Null for every non-integrated status.
    /// </summary>
    public string? Sha { get; init; }

    /// <summary>Integration branch the verdict was computed against (usually "develop").</summary>
    public string IntegrationBranch { get; init; } = "develop";

    /// <summary>
    /// Which of the three integration signals proved the verdict, or the reason a
    /// non-integrated card is pending / conflicted / branch-less. Free-form, for
    /// tooltip + audit only; never drives logic.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// String constants for <see cref="TaskIntegrationStatus.Status"/>. Kept as
/// constants (not an enum) so the JSON wire format is the literal string, matching
/// <see cref="LandedStates"/> / <see cref="TaskTypes"/>.
/// </summary>
public static class IntegrationStatuses
{
    /// <summary>Every attributed commit the card shows is provably in develop (curated merge, or all attributed commits are ancestors).</summary>
    public const string Integrated = "integrated";

    /// <summary>Some — but not all — of the attributed commits the card shows are in develop; the rest have not landed yet.</summary>
    public const string Partial = "partial";

    /// <summary>The task has integrable work that is not (yet) in develop.</summary>
    public const string Pending = "pending";

    /// <summary>The deferred merge-into-develop step recorded a conflict / error; the work was NOT merged.</summary>
    public const string ConflictSkipped = "conflict-skipped";

    /// <summary>The card has no task branch and no attributed commit - nothing to integrate.</summary>
    public const string NoBranch = "no-branch";

    public static readonly string[] All = [Integrated, Partial, Pending, ConflictSkipped, NoBranch];

    /// <summary>
    /// Persisted tag stamped on a card that was accepted (moved into 6-completed)
    /// while its work was not yet in develop (AGT-2202). Tag ids allow only
    /// <c>[a-z0-9-]</c>; the former call-site literal <c>integration:pending</c>
    /// was therefore normalized to this value by <c>SetJobTags</c>. Durable audit
    /// marker: the completed-lane audit lists it and clears it once the card
    /// becomes integrated. Not a hard block.
    /// </summary>
    public const string PendingTag = "integrationpending";

    /// <summary>
    /// Matches the persisted tag and the pre-normalization spelling that can
    /// still occur in hand-authored fixtures or legacy task JSON.
    /// </summary>
    public static bool IsPendingTag(string? tag)
        => string.Equals(tag, PendingTag, StringComparison.OrdinalIgnoreCase)
           || string.Equals(tag, "integration:pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the card carries integrable work that is not (fully) in develop (partial, pending or conflict).</summary>
    public static bool IsNotIntegrated(string? status)
        => string.Equals(status, Partial, StringComparison.Ordinal)
           || string.Equals(status, Pending, StringComparison.Ordinal)
           || string.Equals(status, ConflictSkipped, StringComparison.Ordinal);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return NoBranch;
        var v = value.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return s;
        return NoBranch;
    }
}
