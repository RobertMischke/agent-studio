using Microsoft.Extensions.Logging;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>Result of the worktree pre-step: an isolated worktree on a fresh task branch.</summary>
public sealed record WorktreePreparation(bool Success, string? WorktreePath, string? Branch, string? Error);

public enum IntegrationOutcome
{
    /// <summary>Task branch was folded into the integration branch (direct-merge).</summary>
    Merged,
    /// <summary>Rebase onto the integration tip conflicted; left for escalation / PR fallback.</summary>
    Conflict,
    /// <summary>pull-request strategy: nothing was merged here; the team's review owns it.</summary>
    PushedForReview,
    /// <summary>A git step failed for a reason other than a conflict.</summary>
    Error,
}

/// <summary>Result of the integration (merge) post-step.</summary>
public sealed record IntegrationResult(IntegrationOutcome Outcome, string? IntegratedSha, string? Error)
{
    public bool Merged => Outcome == IntegrationOutcome.Merged;
}

/// <summary>Result of the worktree teardown post-step.</summary>
public sealed record TeardownResult(bool Success, string? Error);

/// <summary>
/// ADR-0052 §3-§6: composes the low-level <see cref="GitService"/> worktree
/// primitives (added in slice 1) into the per-task pipeline steps the parallel
/// runner drives - the worktree <b>pre-step</b> (<see cref="Prepare"/>), the
/// integration / merge <b>post-step</b> (<see cref="Integrate"/>), and the
/// cleanup <b>post-step</b> (<see cref="Teardown"/>). All git lives here, never
/// in the run agent (§4): the agent only edits files inside its worktree.
///
/// <para>
/// The <c>direct-merge</c> strategy rebases the task branch onto the latest
/// integration tip and fast-forwards the integration branch onto it, so history
/// stays linear and a conflict surfaces deterministically (rebase aborts, leaving
/// the worktree clean) rather than as a silent merge commit. The
/// <c>pull-request</c> strategy stops short of merging - PR creation is its own
/// slice (concept §8.6); this step leaves the pushed branch for the team's review
/// gate. The per-project <b>merge-queue</b> that serializes concurrent
/// integrations into one branch at a time is a runner concern (it owns the slots)
/// and is layered on top of this stateless service.
/// </para>
/// </summary>
public sealed class WorktreeTaskLifecycle
{
    private readonly GitService _git;
    private readonly ILogger<WorktreeTaskLifecycle> _logger;

    public WorktreeTaskLifecycle(GitService git, ILogger<WorktreeTaskLifecycle> logger)
    {
        _git = git;
        _logger = logger;
    }

    /// <summary>The ephemeral branch name for a task: <c>task/&lt;sanitized-id&gt;</c>.</summary>
    public static string BranchFor(string taskId) => "task/" + SanitizeId(taskId);

    /// <summary>
    /// Pre-step: cut a fresh <c>task/&lt;id&gt;</c> branch off
    /// <paramref name="integrationBranch"/> and check it out in its own worktree
    /// under <paramref name="worktreeRoot"/>. The shared <c>.git</c> is reused
    /// (no clone). Fails cleanly when the branch already exists or the path is
    /// occupied.
    /// </summary>
    public WorktreePreparation Prepare(string repoRoot, string taskId, string integrationBranch, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new WorktreePreparation(false, null, null, "Task id is required.");
        if (string.IsNullOrWhiteSpace(worktreeRoot))
            return new WorktreePreparation(false, null, null, "Worktree root is required.");

        var branch = BranchFor(taskId);
        var path = Path.Combine(worktreeRoot, SanitizeId(taskId));

        var add = _git.WorktreeAdd(repoRoot, path, branch, integrationBranch);
        if (!add.Success)
        {
            _logger.LogWarning("Worktree prepare failed for task {TaskId} on {Branch}: {Error}", taskId, branch, add.Error);
            return new WorktreePreparation(false, null, branch, add.Error);
        }
        _logger.LogInformation("Prepared worktree for task {TaskId}: branch {Branch} off {From} at {Path}",
            taskId, branch, integrationBranch, add.Path);
        return new WorktreePreparation(true, add.Path, branch, null);
    }

    /// <summary>
    /// Merge post-step: fold the finished <paramref name="taskBranch"/> back into
    /// <paramref name="integrationBranch"/> per <paramref name="strategy"/>.
    /// For <c>direct-merge</c>: rebase the worktree onto the integration tip, then
    /// fast-forward the integration branch (which must be checked out in
    /// <paramref name="repoRoot"/>). A rebase conflict returns
    /// <see cref="IntegrationOutcome.Conflict"/> with the branch and worktree left
    /// intact for the conflict-resolution agent / PR fallback. For
    /// <c>pull-request</c>: returns <see cref="IntegrationOutcome.PushedForReview"/>
    /// without merging.
    /// </summary>
    public IntegrationResult Integrate(
        string repoRoot, string worktreePath, string taskBranch, string integrationBranch, string strategy)
    {
        if (string.Equals(strategy, IntegrationStrategies.PullRequest, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Integration for {Branch} deferred to review (pull-request strategy); not auto-merging into {Integration}",
                taskBranch, integrationBranch);
            return new IntegrationResult(IntegrationOutcome.PushedForReview, null, null);
        }

        var rebase = _git.RebaseOnto(worktreePath, integrationBranch);
        if (!rebase.Success)
        {
            _logger.LogWarning("Integration of {Branch} hit a rebase conflict onto {Integration}: {Error}",
                taskBranch, integrationBranch, rebase.Error);
            return new IntegrationResult(IntegrationOutcome.Conflict, null, rebase.Error);
        }

        var ff = _git.MergeFastForward(repoRoot, taskBranch);
        if (!ff.Success)
        {
            _logger.LogWarning("Fast-forward of {Integration} onto {Branch} failed: {Error}",
                integrationBranch, taskBranch, ff.Error);
            return new IntegrationResult(IntegrationOutcome.Error, null, ff.Error);
        }

        var sha = _git.ReadHeadShaAt(repoRoot);
        _logger.LogInformation("Integrated {Branch} into {Integration} at {Sha}", taskBranch, integrationBranch, sha ?? "<unknown>");
        return new IntegrationResult(IntegrationOutcome.Merged, sha, null);
    }

    /// <summary>
    /// Cleanup post-step: remove the task worktree and, when
    /// <paramref name="deleteBranch"/> is set, drop the task branch ref. The
    /// worktree is removed first because git refuses to delete a branch that is
    /// still checked out in a live worktree. <paramref name="force"/> chooses an
    /// unconditional branch delete (used after the work has been integrated or
    /// abandoned) over the safe merged-only delete. Best-effort: a failure on one
    /// step does not skip the other, and both errors are reported together.
    /// </summary>
    public TeardownResult Teardown(
        string repoRoot, string worktreePath, string? taskBranch, bool deleteBranch, bool force = false)
    {
        string? error = null;

        var rm = _git.WorktreeRemove(repoRoot, worktreePath);
        if (!rm.Success) error = rm.Error;

        if (deleteBranch && !string.IsNullOrWhiteSpace(taskBranch))
        {
            var del = _git.DeleteBranch(repoRoot, taskBranch!, force);
            if (!del.Success) error = error is null ? del.Error : $"{error}; {del.Error}";
        }

        if (error != null)
            _logger.LogWarning("Worktree teardown for {Path} reported: {Error}", worktreePath, error);
        return new TeardownResult(error is null, error);
    }

    /// <summary>
    /// Reduces a task id / slug to a git-safe branch + path segment: letters,
    /// digits, and <c>-_.</c> survive; everything else becomes a hyphen; leading
    /// and trailing separators are trimmed. Keeps the result inside what
    /// <see cref="GitService"/>'s branch-name guard accepts.
    /// </summary>
    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "task";
        var chars = id.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-', '.', '/');
        return string.IsNullOrEmpty(s) ? "task" : s;
    }
}
