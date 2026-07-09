

namespace AgentStudio.Tasks;

/// <summary>
/// Owns the task commit-provenance record (ASS-1724, epic ASS-1720): the
/// append-only <c>provenance</c> block on <c>task.json</c> plus the read-time
/// projection (<see cref="TaskProvenanceView"/>) that the board uses to answer
/// "where does this work live" across the worktree -&gt; develop -&gt; main path.
///
/// <para>
/// Two halves, deliberately split so the write side stays cheap and the read
/// side stays graph-fresh:
/// <list type="bullet">
/// <item><see cref="RecordTransition"/> is the ONE recording hook. It is called
///   from <see cref="TaskTransitionService"/> after a lane move lands and appends
///   a single anchor (branch tip + integration head at that instant). Nothing
///   else writes transitions, so the ladder can be reconstructed historically
///   from one ordered list.</item>
/// <item><see cref="BuildView"/> recomputes the derived facts live off the graph
///   - <see cref="DeriveLandedState"/> and the per-commit merge-set - because
///   those go stale the moment develop / main move. The derived
///   <c>landedState</c> is intentionally never persisted (see
///   <see cref="TaskProvenance"/>).</item>
/// </list>
/// </para>
///
/// <para>
/// The graph queries reuse the existing <see cref="GitService"/> primitives
/// (<see cref="GitService.GetBranchTip"/>, <see cref="GitService.GetMergeBase"/>,
/// <see cref="GitService.IsAncestor"/>,
/// <see cref="GitService.GetCommitsInRangeAtRoot"/>). Recording is best-effort
/// and fully guarded: a git or disk failure must never block the lane
/// transition that already completed.
/// </para>
/// </summary>
public sealed class TaskProvenanceService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<TaskProvenanceService> _logger;

    public const string ReleaseBranch = "main";

    public TaskProvenanceService(
        GitService git,
        ProjectSettingsService settings,
        TaskMutationService mutations,
        ILogger<TaskProvenanceService> logger)
    {
        _git = git;
        _settings = settings;
        _mutations = mutations;
        _logger = logger;
    }

    /// <summary>
    /// The ONE recording hook. Appends a single <see cref="TaskProvenanceTransition"/>
    /// anchor for the lane the task just entered, pinning the
    /// <c>task/&lt;id&gt;</c> tip and the integration-branch head at that instant.
    /// The <see cref="TaskProvenance.Base"/> fork point is captured once, on the
    /// first anchor that can see the branch. Best-effort: any failure is logged
    /// and swallowed so the move that already landed is never undone.
    /// </summary>
    public void RecordTransition(TaskInfo info, string targetLane)
    {
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath);
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
            var integrationBranch = ResolveIntegrationBranch(info.ProjectName, repoRoot);

            var branchTip = _git.GetBranchTip(repoRoot, branch);
            var workBranchHead = HeadOf(repoRoot, integrationBranch);

            var existing = info.Provenance;
            // Capture the fork point once, the first time the branch is visible.
            var baseSha = existing?.Base;
            if (string.IsNullOrWhiteSpace(baseSha) && branchTip != null)
                baseSha = _git.GetMergeBase(repoRoot, branch, integrationBranch)
                          ?? _git.GetMergeBase(repoRoot, branch, "origin/" + integrationBranch);

            var transition = new TaskProvenanceTransition
            {
                Lane = targetLane,
                AtUtc = DateTime.UtcNow,
                BranchTip = branchTip,
                WorkBranchHead = workBranchHead,
            };

            var merged = AppendTransition(existing, branch, baseSha, transition);
            _mutations.SetProvenanceOnFolder(info.FolderPath, merged);

            _logger.LogInformation(
                "provenance-recorded job={JobId} lane={Lane} branch={Branch} branchTip={BranchTip} base={Base}",
                info.Id, targetLane, branch,
                branchTip == null ? "(none)" : Short(branchTip),
                baseSha == null ? "(none)" : Short(baseSha));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "provenance-record failed for {JobId} entering {Lane}", info.Id, targetLane);
        }
    }

    /// <summary>
    /// Pure append: returns a new <see cref="TaskProvenance"/> with
    /// <paramref name="transition"/> added to the end of the transition list. The
    /// <see cref="TaskProvenance.Base"/> fork point is write-once (an existing base
    /// is never overwritten); <see cref="TaskProvenance.Merge"/> is carried through
    /// untouched (it is owned by sibling slice ASS-1721). Side-effect-free so it is
    /// trivially unit-testable.
    /// </summary>
    public static TaskProvenance AppendTransition(
        TaskProvenance? existing,
        string branch,
        string? baseSha,
        TaskProvenanceTransition transition)
    {
        var transitions = existing?.Transitions is { Count: > 0 }
            ? new List<TaskProvenanceTransition>(existing.Transitions)
            : new List<TaskProvenanceTransition>();
        transitions.Add(transition);

        return new TaskProvenance
        {
            Branch = string.IsNullOrWhiteSpace(existing?.Branch) ? branch : existing!.Branch,
            Base = string.IsNullOrWhiteSpace(existing?.Base) ? baseSha : existing!.Base,
            Transitions = transitions,
            Merge = existing?.Merge,
        };
    }

    /// <summary>
    /// Records the develop-merge fact (ASS-1721 / ASS-1752): writes the
    /// <see cref="TaskProvenanceMerge"/> block onto the task's provenance the first
    /// time the <c>task/&lt;id&gt;</c> branch is folded into the integration branch.
    /// This is the one persisted "landed" signal the board card can read without a
    /// per-card graph query, so it must be written from a caller that already holds
    /// a FRESH <see cref="TaskInfo"/> (the on-disk write is replace-all, so a stale
    /// <see cref="TaskInfo.Provenance"/> would drop earlier transitions). Write-once
    /// like <see cref="TaskProvenance.Base"/>: an already-recorded merge is never
    /// overwritten. Best-effort and fully guarded - a failure here can never undo
    /// the merge that already landed.
    /// </summary>
    public void RecordMerge(TaskInfo info, string? mergeSha, string? beforeSha = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mergeSha)) return;
            if (info.Provenance?.Merge?.MergeCommit is { Length: > 0 }) return; // write-once

            var merge = new TaskProvenanceMerge
            {
                MergeCommit = mergeSha,
                WorkBranchHeadBefore = beforeSha,
                WorkBranchHeadAfter = mergeSha,
                AtUtc = DateTime.UtcNow,
            };

            var merged = WithMerge(info.Provenance, WorktreeTaskLifecycle.BranchFor(info.Id), merge);
            _mutations.SetProvenanceOnFolder(info.FolderPath, merged);

            _logger.LogInformation(
                "provenance-merge-recorded job={JobId} mergeCommit={Merge} before={Before}",
                info.Id, Short(mergeSha), beforeSha == null ? "(none)" : Short(beforeSha));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "provenance-merge-record failed for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Pure write-once merge anchor: returns a new <see cref="TaskProvenance"/> with
    /// <paramref name="merge"/> set, carrying the branch, base, and transitions
    /// through untouched. An existing <see cref="TaskProvenance.Merge"/> is never
    /// overwritten (append-only fact). Side-effect-free so it is trivially
    /// unit-testable, matching <see cref="AppendTransition"/>.
    /// </summary>
    public static TaskProvenance WithMerge(
        TaskProvenance? existing,
        string branch,
        TaskProvenanceMerge merge)
    {
        var transitions = existing?.Transitions is { Count: > 0 }
            ? new List<TaskProvenanceTransition>(existing.Transitions)
            : new List<TaskProvenanceTransition>();

        return new TaskProvenance
        {
            Branch = string.IsNullOrWhiteSpace(existing?.Branch) ? branch : existing!.Branch,
            Base = existing?.Base,
            Transitions = transitions,
            Merge = existing?.Merge ?? merge,
        };
    }

    /// <summary>
    /// Graph-based landed-state for an anchor SHA. Returns
    /// <see cref="LandedStates.ReleasedToMain"/> when the anchor is an ancestor of
    /// the release branch, <see cref="LandedStates.MergedToDevelop"/> when it is an
    /// ancestor of the integration branch, otherwise
    /// <see cref="LandedStates.OnBranchOnly"/> (including when there is no anchor at
    /// all). Both local and <c>origin/</c>-prefixed refs are checked, so a fresh
    /// clone with only remote-tracking branches still resolves. Static + takes the
    /// <see cref="GitService"/> so tests can drive it against a throwaway repo
    /// without constructing the full service.
    /// </summary>
    public static string DeriveLandedState(
        GitService git,
        string repoRoot,
        string? anchorSha,
        string integrationBranch,
        string releaseBranch = ReleaseBranch)
    {
        if (string.IsNullOrWhiteSpace(anchorSha)) return LandedStates.OnBranchOnly;
        if (IsContainedIn(git, repoRoot, anchorSha, releaseBranch)) return LandedStates.ReleasedToMain;
        if (IsContainedIn(git, repoRoot, anchorSha, integrationBranch)) return LandedStates.MergedToDevelop;
        return LandedStates.OnBranchOnly;
    }

    /// <summary>
    /// Read-time projection for <c>GET /api/tasks/{id}/provenance</c>: the
    /// persisted facts plus the live-derived landed-state, landed ladder, and
    /// per-commit branch membership. Never persisted; recomputed on every read so
    /// it never lies about where develop / main currently are.
    /// </summary>
    public TaskProvenanceView BuildView(TaskInfo info)
    {
        var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
        var prov = info.Provenance;
        var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath);
        var integrationBranch = ResolveIntegrationBranch(info.ProjectName, repoRoot);

        // No resolvable repo (e.g. project not configured for git): surface the
        // persisted facts with an empty derived view rather than throwing.
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return new TaskProvenanceView
            {
                Branch = prov?.Branch ?? branch,
                Base = prov?.Base,
                Transitions = prov?.Transitions ?? [],
                Merge = prov?.Merge,
                LandedState = LandedStates.OnBranchOnly,
                Ladder = new TaskLandedLadder { Branch = prov?.Branch ?? branch, IntegrationBranch = integrationBranch, ReleaseBranch = ReleaseBranch },
                Commits = FallbackMembership(info, null, integrationBranch),
            };
        }

        var liveBranchTip = _git.GetBranchTip(repoRoot, branch);
        // Anchor for the landed-state: prefer the live branch tip, fall back to
        // the newest recorded transition anchor, then to the latest known commit
        // SHA (sequential runs that never cut a task/<id> branch).
        var anchorSha = liveBranchTip
            ?? prov?.Transitions.LastOrDefault(t => !string.IsNullOrWhiteSpace(t.BranchTip))?.BranchTip
            ?? info.Commits.LastOrDefault()?.Sha
            ?? info.Commit?.Sha;

        var integrationHead = HeadOf(repoRoot, integrationBranch);
        var releaseHead = HeadOf(repoRoot, ReleaseBranch);

        var mergedToIntegration = !string.IsNullOrWhiteSpace(anchorSha)
            && IsContainedIn(_git, repoRoot, anchorSha!, integrationBranch);
        var releasedToRelease = !string.IsNullOrWhiteSpace(anchorSha)
            && IsContainedIn(_git, repoRoot, anchorSha!, ReleaseBranch);

        var landedState = releasedToRelease
            ? LandedStates.ReleasedToMain
            : mergedToIntegration ? LandedStates.MergedToDevelop : LandedStates.OnBranchOnly;

        var ladder = new TaskLandedLadder
        {
            Branch = branch,
            BranchTip = liveBranchTip,
            IntegrationBranch = integrationBranch,
            IntegrationHead = integrationHead,
            MergedToIntegration = mergedToIntegration,
            ReleaseBranch = ReleaseBranch,
            ReleaseHead = releaseHead,
            ReleasedToRelease = releasedToRelease,
        };

        var commits = BuildCommitMembership(info, repoRoot, branch, liveBranchTip, prov?.Base, integrationBranch);

        return new TaskProvenanceView
        {
            Branch = prov?.Branch ?? branch,
            Base = prov?.Base,
            Transitions = prov?.Transitions ?? [],
            Merge = prov?.Merge,
            LandedState = landedState,
            Ladder = ladder,
            Commits = commits,
        };
    }

    /// <summary>
    /// Per-commit branch membership for the task's merge-set. Graph path: the
    /// commits <c>task/&lt;id&gt;</c> is ahead of its <c>base</c> fork point
    /// (<c>base..branch</c>). Falls back to the persisted attributed commit chain
    /// when the branch / base is unavailable (sequential run). Each commit is
    /// tagged with whether it is also reachable from develop / main.
    /// </summary>
    private List<TaskCommitMembership> BuildCommitMembership(
        TaskInfo info,
        string repoRoot,
        string branch,
        string? liveBranchTip,
        string? baseSha,
        string integrationBranch)
    {
        if (liveBranchTip != null && !string.IsNullOrWhiteSpace(baseSha))
        {
            var range = _git.GetCommitsInRangeAtRoot(repoRoot, baseSha!, branch);
            if (range.Count > 0)
            {
                return range.Select(c => new TaskCommitMembership
                {
                    Sha = c.Sha,
                    ShortSha = c.ShortSha,
                    Message = c.Subject,
                    OnTaskBranch = true,
                    AlsoOnIntegration = IsContainedIn(_git, repoRoot, c.Sha, integrationBranch),
                    AlsoOnRelease = IsContainedIn(_git, repoRoot, c.Sha, ReleaseBranch),
                }).ToList();
            }
        }

        return FallbackMembership(info, repoRoot, integrationBranch);
    }

    /// <summary>
    /// Fallback merge-set from the persisted attributed commit chain when there is
    /// no live branch to walk. Membership is still graph-checked when a repo root
    /// is available so a sequential-run task whose commits landed on develop still
    /// reads correctly.
    /// </summary>
    private List<TaskCommitMembership> FallbackMembership(TaskInfo info, string? repoRoot, string integrationBranch)
    {
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };

        return chain
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha))
            .Select(c => new TaskCommitMembership
            {
                Sha = c.Sha,
                ShortSha = string.IsNullOrWhiteSpace(c.ShortSha) ? Short(c.Sha) : c.ShortSha,
                Message = c.Message,
                OnTaskBranch = true,
                AlsoOnIntegration = repoRoot != null && IsContainedIn(_git, repoRoot, c.Sha, integrationBranch),
                AlsoOnRelease = repoRoot != null && IsContainedIn(_git, repoRoot, c.Sha, ReleaseBranch),
            })
            .ToList();
    }

    private string ResolveIntegrationBranch(string projectName, string? repoRoot)
    {
        var configured = _settings.Get(projectName).IntegrationBranch;
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return _git.ResolveIntegrationBranch(repoRoot, configured);
        return string.IsNullOrWhiteSpace(configured) ? new ProjectSettings().IntegrationBranch : configured;
    }

    /// <summary>Live head of a branch, checking the local ref then its <c>origin/</c> mirror.</summary>
    private string? HeadOf(string repoRoot, string branch)
        => _git.GetBranchTip(repoRoot, branch) ?? _git.GetBranchTip(repoRoot, "origin/" + branch);

    private static bool IsContainedIn(GitService git, string repoRoot, string sha, string branch)
        => git.IsAncestor(repoRoot, sha, branch) || git.IsAncestor(repoRoot, sha, "origin/" + branch);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
