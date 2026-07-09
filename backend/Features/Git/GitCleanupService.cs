namespace AgentStudio.Git;

/// <summary>
/// What a cleanup candidate is, so the frontend can group and label the plan
/// and the executor can dispatch the right teardown primitive.
/// </summary>
public enum CleanupTargetKind
{
    /// <summary>A local <c>task/&lt;id&gt;</c> branch head (<c>git branch -D</c>).</summary>
    LocalBranch,
    /// <summary>A <c>origin/task/&lt;id&gt;</c> remote branch (<c>git push --delete</c>).</summary>
    RemoteBranch,
    /// <summary>A <c>refs/backups/*</c> operational safety-net ref (<c>git update-ref -d</c>).</summary>
    BackupRef,
    /// <summary>A registered worktree whose directory is gone (<c>git worktree prune</c>).</summary>
    StaleWorktree,
}

/// <summary>Merge status of a candidate against the integration branch.</summary>
public enum CleanupMergeStatus
{
    /// <summary>The tip is already an ancestor of the integration branch - safe to drop.</summary>
    Merged,
    /// <summary>The tip is NOT contained in the integration branch - never touched (AGT-1945).</summary>
    Unmerged,
    /// <summary>Merge status does not apply (a stale worktree registration).</summary>
    NotApplicable,
}

/// <summary>
/// One row in the cleanup dry-run preview. <see cref="Eligible"/> is the single
/// server-side gate: a candidate is eligible only when it is provably merged
/// into the integration branch (or, for a stale worktree, its directory is
/// gone) and nothing else blocks its removal. <see cref="Reason"/> carries the
/// merge evidence when eligible, and the reason it is kept otherwise, so the
/// operator sees WHY every line is or is not in the delete set.
/// </summary>
public record CleanupCandidate(
    CleanupTargetKind Kind,
    string Name,
    string? Remote,
    string? TipSha,
    string? TipShortSha,
    CleanupMergeStatus MergeStatus,
    bool Eligible,
    string Reason);

/// <summary>
/// The cleanup dry-run plan for one project: every <c>task/*</c> branch (local +
/// remote), every <c>refs/backups/*</c> ref, and every stale worktree
/// registration, each classified merged / unmerged / stale against
/// <see cref="IntegrationBranch"/>. Read-only - nothing is deleted until
/// <see cref="GitCleanupService.Execute"/> is called with an explicit selection.
/// <see cref="IsRepo"/> false with a populated <see cref="Error"/> is the
/// empty/error signal (unknown project, no repository, non-git folder).
/// </summary>
public record GitCleanupPlan(
    string ProjectName,
    string? RepositoryPath,
    bool IsRepo,
    string IntegrationBranch,
    IReadOnlyList<CleanupCandidate> Candidates,
    string? Error);

/// <summary>One item the operator confirmed for deletion in the execute request.</summary>
public record CleanupExecutionItem(string Kind, string Name, string? Remote);

/// <summary>The confirmed selection posted to <see cref="GitCleanupService.Execute"/>.</summary>
public record GitCleanupRequest(IReadOnlyList<CleanupExecutionItem> Items);

/// <summary>Per-item outcome of an executed cleanup.</summary>
public record CleanupActionOutcome(
    CleanupTargetKind Kind,
    string Name,
    string? Remote,
    bool Deleted,
    string Reason);

/// <summary>
/// Result report of an executed cleanup: how many refs/worktrees were dropped,
/// how many were kept (and why), and the per-item detail. <see cref="IsRepo"/>
/// false with <see cref="Error"/> when the project could not be resolved.
/// </summary>
public record GitCleanupResult(
    string ProjectName,
    string IntegrationBranch,
    bool IsRepo,
    int DeletedCount,
    int KeptCount,
    IReadOnlyList<CleanupActionOutcome> Actions,
    string? Error);

/// <summary>
/// Git-Management cleanup for the Project Hub: analyses which merged
/// <c>task/*</c> branches, remote task branches, <c>refs/backups/*</c> refs and
/// stale worktree registrations can be pruned, and executes an operator-confirmed
/// subset. Composes the existing <see cref="GitService"/> primitives
/// (<see cref="GitService.IsAncestor"/>, <see cref="GitService.DeleteBranch"/>,
/// <see cref="GitService.DeleteRemoteBranch"/>, <see cref="GitService.DeleteRef"/>,
/// <see cref="GitService.WorktreePrune"/>) rather than re-forking git itself.
///
/// <para>
/// Invariant (AGT-1945): only GEMERGTES is ever deleted. Every candidate's
/// merge status is proven with <c>merge-base --is-ancestor</c> against the
/// integration branch, and <see cref="Execute"/> re-derives eligibility from a
/// fresh plan and re-checks ancestry immediately before each branch/ref delete -
/// so a stale or hand-crafted request can never drop unmerged work. Unmerged
/// branches and backup refs whose commit is not yet in the integration branch
/// are never touched; they are reported as kept with the reason.
/// </para>
/// </summary>
public sealed class GitCleanupService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<GitCleanupService> _logger;

    private const string LocalTaskPattern = "refs/heads/task";
    private const string RemoteTaskPattern = "refs/remotes/origin/task";
    private const string BackupPattern = "refs/backups";
    private const string RemotePrefix = "origin/";
    private const string DefaultRemote = "origin";

    public GitCleanupService(
        GitService git,
        ProjectSettingsService settings,
        ILogger<GitCleanupService> logger)
    {
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Builds the read-only dry-run plan for a project. Never mutates the
    /// repository. Every candidate carries its merge evidence so the preview can
    /// explain what would be deleted and what would be kept.
    /// </summary>
    public GitCleanupPlan BuildPlan(string projectName)
    {
        var ctx = ResolveContext(projectName);
        if (ctx == null)
            return new GitCleanupPlan(projectName, null, false, "develop", [], "Project has no resolvable git repository.");

        var (root, integration) = ctx.Value;
        var candidates = new List<CleanupCandidate>();

        var worktrees = _git.ListWorktrees(root);
        // Branches that are currently checked out (in the primary checkout or any
        // worktree whose folder still exists) can't be deleted; keep them out of
        // the delete set with a clear reason.
        var checkedOutBranches = worktrees
            .Where(w => !string.IsNullOrEmpty(w.Branch) && Directory.Exists(w.Path))
            .GroupBy(w => w.Branch!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);

        // ---- local task/* branches ----
        foreach (var refLine in _git.ListRefs(root, LocalTaskPattern))
        {
            var name = refLine.ShortName;
            if (string.IsNullOrEmpty(name) || string.Equals(name, integration, StringComparison.Ordinal)) continue;

            var merged = _git.IsAncestor(root, name, integration);
            if (checkedOutBranches.TryGetValue(name, out var wtPath))
            {
                candidates.Add(new CleanupCandidate(
                    CleanupTargetKind.LocalBranch, name, null, refLine.Sha, refLine.ShortSha,
                    merged ? CleanupMergeStatus.Merged : CleanupMergeStatus.Unmerged, false,
                    $"Checked out in worktree {wtPath}; remove the worktree first."));
                continue;
            }

            candidates.Add(new CleanupCandidate(
                CleanupTargetKind.LocalBranch, name, null, refLine.Sha, refLine.ShortSha,
                merged ? CleanupMergeStatus.Merged : CleanupMergeStatus.Unmerged, merged,
                merged
                    ? $"Merged into {integration} (tip {refLine.ShortSha} is an ancestor)."
                    : $"Not merged into {integration}; kept (AGT-1945 invariant)."));
        }

        // ---- remote origin/task/* branches ----
        foreach (var refLine in _git.ListRefs(root, RemoteTaskPattern))
        {
            var shortName = refLine.ShortName; // e.g. origin/task/42
            if (!shortName.StartsWith(RemotePrefix, StringComparison.Ordinal)) continue;
            var branch = shortName[RemotePrefix.Length..];
            if (string.IsNullOrEmpty(branch) || string.Equals(branch, integration, StringComparison.Ordinal)) continue;

            var merged = _git.IsAncestor(root, shortName, integration);
            candidates.Add(new CleanupCandidate(
                CleanupTargetKind.RemoteBranch, branch, DefaultRemote, refLine.Sha, refLine.ShortSha,
                merged ? CleanupMergeStatus.Merged : CleanupMergeStatus.Unmerged, merged,
                merged
                    ? $"Merged into {integration}; remote copy {shortName} is redundant."
                    : $"Not merged into {integration}; kept (AGT-1945 invariant)."));
        }

        // ---- refs/backups/* ----
        foreach (var refLine in _git.ListRefs(root, BackupPattern))
        {
            var contained = _git.IsAncestor(root, refLine.FullName, integration);
            candidates.Add(new CleanupCandidate(
                CleanupTargetKind.BackupRef, refLine.FullName, null, refLine.Sha, refLine.ShortSha,
                contained ? CleanupMergeStatus.Merged : CleanupMergeStatus.Unmerged, contained,
                contained
                    ? $"Backup commit {refLine.ShortSha} is contained in {integration}."
                    : $"Backup commit {refLine.ShortSha} is not in {integration}; kept."));
        }

        // ---- stale (orphaned) worktree registrations ----
        foreach (var wt in worktrees)
        {
            if (wt.IsPrimary) continue;
            if (Directory.Exists(wt.Path)) continue;
            candidates.Add(new CleanupCandidate(
                CleanupTargetKind.StaleWorktree, wt.Path, null, wt.HeadSha, wt.HeadShortSha,
                CleanupMergeStatus.NotApplicable, true,
                "Worktree directory is missing; the registration is stale and can be pruned."));
        }

        _logger.LogInformation(
            "git-cleanup-plan project={Project} integration={Integration} candidates={Count} eligible={Eligible}",
            projectName, integration, candidates.Count, candidates.Count(c => c.Eligible));

        return new GitCleanupPlan(projectName, root, true, integration, candidates, null);
    }

    /// <summary>
    /// Executes the operator-confirmed subset. Recomputes the plan fresh, so only
    /// candidates that are still provably eligible are acted on; a requested item
    /// that is not eligible (unmerged, checked out, or vanished) is reported as
    /// kept, never deleted. Branch and backup-ref deletions re-check ancestry one
    /// last time immediately before the delete.
    /// </summary>
    public GitCleanupResult Execute(string projectName, GitCleanupRequest request)
    {
        var ctx = ResolveContext(projectName);
        if (ctx == null)
            return new GitCleanupResult(projectName, "develop", false, 0, 0, [], "Project has no resolvable git repository.");

        var (root, integration) = ctx.Value;
        var plan = BuildPlan(projectName);
        var eligible = plan.Candidates
            .Where(c => c.Eligible)
            .ToDictionary(CandidateKey, c => c, StringComparer.Ordinal);

        var actions = new List<CleanupActionOutcome>();
        var pruned = false;
        var items = request?.Items ?? [];

        foreach (var item in items)
        {
            if (!Enum.TryParse<CleanupTargetKind>(item.Kind, ignoreCase: true, out var kind))
            {
                actions.Add(new CleanupActionOutcome(CleanupTargetKind.LocalBranch, item.Name, item.Remote, false,
                    $"Unknown cleanup kind '{item.Kind}'."));
                continue;
            }

            var key = ItemKey(kind, item.Name, item.Remote);
            if (!eligible.TryGetValue(key, out var candidate))
            {
                actions.Add(new CleanupActionOutcome(kind, item.Name, item.Remote, false,
                    $"No longer eligible for deletion (must be merged into {integration}); kept."));
                continue;
            }

            actions.Add(DeleteCandidate(root, integration, candidate, ref pruned));
        }

        var deleted = actions.Count(a => a.Deleted);
        var kept = actions.Count - deleted;
        _logger.LogInformation(
            "git-cleanup-executed project={Project} integration={Integration} requested={Requested} deleted={Deleted} kept={Kept}",
            projectName, integration, items.Count, deleted, kept);

        return new GitCleanupResult(projectName, integration, true, deleted, kept, actions, null);
    }

    private CleanupActionOutcome DeleteCandidate(string root, string integration, CleanupCandidate c, ref bool pruned)
    {
        switch (c.Kind)
        {
            case CleanupTargetKind.LocalBranch:
            {
                // Belt-and-suspenders: prove ancestry once more right before the
                // irreversible delete so a race that merged nothing can't slip through.
                if (!_git.IsAncestor(root, c.Name, integration))
                    return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, false,
                        $"Ancestry re-check failed; {c.Name} is not merged into {integration}. Kept.");
                var r = _git.DeleteBranch(root, c.Name, force: true);
                return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, r.Success,
                    r.Success ? $"Deleted local branch (merged into {integration})." : r.Error ?? "Delete failed.");
            }
            case CleanupTargetKind.RemoteBranch:
            {
                var remote = string.IsNullOrWhiteSpace(c.Remote) ? DefaultRemote : c.Remote!;
                var r = _git.DeleteRemoteBranch(root, c.Name, remote);
                return new CleanupActionOutcome(c.Kind, c.Name, remote, r.Success,
                    r.Success ? $"Deleted remote branch {remote}/{c.Name} (merged into {integration})." : r.Error ?? "Delete failed.");
            }
            case CleanupTargetKind.BackupRef:
            {
                if (!_git.IsAncestor(root, c.Name, integration))
                    return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, false,
                        $"Ancestry re-check failed; {c.Name} is not contained in {integration}. Kept.");
                var r = _git.DeleteRef(root, c.Name);
                return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, r.Success,
                    r.Success ? $"Deleted backup ref (contained in {integration})." : r.Error ?? "Delete failed.");
            }
            case CleanupTargetKind.StaleWorktree:
            {
                // git worktree prune drops every stale registration in one pass;
                // run it once even if several stale worktrees were selected.
                if (!pruned)
                {
                    _git.WorktreePrune(root);
                    pruned = true;
                }
                return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, true,
                    "Pruned stale worktree registration (directory was missing).");
            }
            default:
                return new CleanupActionOutcome(c.Kind, c.Name, c.Remote, false, "Unsupported cleanup kind.");
        }
    }

    private (string Root, string Integration)? ResolveContext(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        var root = _git.ResolveProjectRepoRoot(projectName);
        if (string.IsNullOrWhiteSpace(root)) return null;
        var integration = _git.ResolveIntegrationBranch(root, _settings.Get(projectName).IntegrationBranch);
        return (root, integration);
    }

    private static string CandidateKey(CleanupCandidate c) => ItemKey(c.Kind, c.Name, c.Remote);

    private static string ItemKey(CleanupTargetKind kind, string name, string? remote)
        => $"{kind}{remote ?? ""}{name}";
}
