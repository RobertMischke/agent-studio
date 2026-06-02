using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Application-owned job state transitions that need side effects around the
/// raw folder move. Manual API moves and automatic runner completion both use
/// this service so lifecycle policy stays in code, not in agent prompts.
/// </summary>
public sealed class TaskTransitionService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<TaskTransitionService> _logger;
    private readonly TaskSessionLog? _sessions;
    private readonly CompletedPushQueue? _pushQueue;
    private readonly OrchestratorApi.Services.Drift.DriftPostStepRunner? _driftRunner;

    /// <summary>
    /// Fires after a successful folder move with the resolved project name,
    /// the job id, the source state (the lane the job was in before the move),
    /// and the target state. Subscribers must be cheap and side-effect-only;
    /// the move itself is already on disk by the time this fires. The
    /// load-bearing subscriber is the runner-active-state clearer wired in
    /// <c>Program.cs</c>: when the moved job was the active one for that
    /// project, the runner's in-memory <c>_activeJobId</c> is reconciled
    /// atomically so the next pickup tick is unblocked.
    /// </summary>
    public event Action<string, string, string, string>? OnJobMoved;

    public TaskTransitionService(
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<TaskTransitionService> logger,
        TaskSessionLog? sessions = null,
        CompletedPushQueue? pushQueue = null,
        OrchestratorApi.Services.Drift.DriftPostStepRunner? driftRunner = null)
    {
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
        _sessions = sessions;
        _pushQueue = pushQueue;
        _driftRunner = driftRunner;
    }

    /// <summary>
    /// Moves a job between states. When auto-commit is enabled and the
    /// transition is <c>3-progress -> 4-auto-review</c>, commits the target
    /// working tree first and stamps the produced SHA onto the moved job.
    /// (Post-ADR-0025 the lane is 4-auto-review; the orchestrator's
    /// review-decision pass then decides whether to promote to
    /// 5-human-review.)
    /// </summary>
    public async Task<MoveJobOutcome> MoveAsync(
        string jobId,
        string targetState,
        string? watchPath,
        CancellationToken ct = default,
        int? targetIndex = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);

        var settings = _settings.Get(info.ProjectName);
        var autoPushStrategy = AutoPushStrategies.Normalize(settings.AutoPushStrategy);

        // Read-only modes (planning / research) skip every git side effect on the
        // transition: no auto-commit, no immediate push, no commit-attribution,
        // no completed-push. Such a run produces only a report; the runner
        // reports any stray working-tree diff as a containment violation rather
        // than committing it. See TaskModes / ParallelSlotPolicy and ADR-0052.
        var isReadOnly = TaskModes.IsReadOnly(info.Mode);

        var shouldAutoCommit =
            !isReadOnly &&
            info.State == TaskStates.Progress &&
            targetState == TaskStates.AutoReview &&
            settings.AutoCommit;

        TaskCommitInfo? commitToStamp = null;
        if (shouldAutoCommit)
        {
            commitToStamp = await TryAutoCommitAsync(jobId, watchPath, ct);
            if (commitToStamp != null && autoPushStrategy == AutoPushStrategies.AlwaysImmediate)
            {
                await TryPushCommitAsync(commitToStamp.Sha, info.WatchPath, jobId, "auto-commit", ct);
            }
        }

        var fromState = info.State;
        var projectName = info.ProjectName;

        var outcome = _states.MoveJob(jobId, targetState, watchPath);
        if (outcome.Status == MoveJobStatus.Success && commitToStamp != null)
        {
            var moved = _scanner.FindJob(jobId, watchPath);
            if (moved != null)
            {
                _mutations.SetJobCommitOnFolder(moved.FolderPath, commitToStamp);
            }
        }

        // Deterministic commit-attribution post-step (ADR "Commit-Attribution-Regel").
        // Runs on every 3-progress -> 4-auto-review transition, after the
        // auto-commit stamp, so the task's commit set is pinned to its own
        // work and noise that landed in its run windows (crash-recovery for
        // another task, update-stable bumps, merges) is excluded before the
        // review lane renders it. No LLM, no tokens; same git + windows in,
        // same result out, so re-running is a no-op.
        if (outcome.Status == MoveJobStatus.Success
            && info.State == TaskStates.Progress && targetState == TaskStates.AutoReview)
        {
            var attributed = _scanner.FindJob(jobId, watchPath);
            // Commit-attribution is a git step; skip it for read-only runs (they
            // produce no commits to attribute). Drift below is not a git step and
            // self-gates, so it still runs.
            if (attributed != null && !isReadOnly) RunCommitAttribution(attributed, watchPath);

            // DRIFT Nachtrag: fire the enabled drift dimensions as automatic
            // post-steps once the task has settled into auto-review. Fire-and-
            // forget and fully guarded - a drift failure (or the absence of any
            // enabled dimension; the runner self-gates default-OFF) must never
            // affect the lane transition that already completed above.
            if (attributed != null && _driftRunner != null)
            {
                TriggerDriftPostSteps(attributed, settings);
            }
        }

        // Drag-and-drop carries a desired slot in the target lane. Without
        // it the moved folder keeps its source-lane order value, so the
        // card snaps to whatever position that value sorts to in the new
        // lane - not where the user dropped it. Apply the slot after the
        // folder is on disk so the lane scan sees the moved job in its
        // new state when it rewrites every sibling's order field.
        if (outcome.Status == MoveJobStatus.Success && targetIndex.HasValue && fromState != targetState)
        {
            _states.SetOrderInLane(jobId, watchPath, targetIndex.Value);
        }

        if (outcome.Status == MoveJobStatus.Success && fromState != targetState)
        {
            if (targetState == TaskStates.Completed && autoPushStrategy != AutoPushStrategies.Never && !isReadOnly)
            {
                var moved = _scanner.FindJob(jobId, watchPath);
                if (moved != null)
                {
                    // Offload the network push (git fetch + git push, ~2-3 s)
                    // to the background worker so the move-to-complete request
                    // returns immediately. The SHAs are immutable, so the
                    // deferred push is always still correct; the periodic
                    // backstop covers anything dropped on shutdown. When no
                    // queue is wired (unit-test fixtures) we fall back to the
                    // synchronous push so the behaviour is still exercised.
                    if (_pushQueue != null)
                        _pushQueue.Enqueue(new CompletedPushRequest(moved, autoPushStrategy));
                    else
                        await PushCompletedJobCommitsAsync(moved, autoPushStrategy, ct);
                }
            }

            try
            {
                OnJobMoved?.Invoke(projectName, jobId, fromState, targetState);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnJobMoved subscriber threw for {JobId} ({From} -> {To})", jobId, fromState, targetState);
            }
        }

        return outcome;
    }

    /// <summary>
    /// Per-item-atomic batch move. Each item independently routes through
    /// <see cref="MoveAsync"/>, so a failure on one item never rolls back
    /// items that already moved. The result list mirrors the input order
    /// one-for-one with a typed per-item status (<c>moved</c> / <c>not-found</c>
    /// / <c>conflict</c> / <c>rejected</c> / <c>failed</c>). This is the
    /// endpoint backing <c>POST /api/tasks/batch-move</c> and exists so the
    /// LLM never has to fall back to shell <c>mv</c> for a multi-job
    /// restore - the 2026-05-08 incident that motivated this method.
    /// </summary>
    public async Task<IReadOnlyList<BatchMoveItemResult>> BatchMoveAsync(
        IEnumerable<BatchMoveItem> items,
        CancellationToken ct = default)
    {
        var results = new List<BatchMoveItemResult>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.JobId))
            {
                results.Add(new BatchMoveItemResult
                {
                    JobId = item.JobId ?? "",
                    Status = "rejected",
                    Message = "jobId is required"
                });
                continue;
            }

            if (!TaskStates.All.Contains(item.TargetState))
            {
                var msg = TaskStates.NumberedLegacyMap.TryGetValue(item.TargetState, out var renamed)
                    ? $"Lane '{item.TargetState}' was renamed in ADR-0025. Use '{renamed}' instead."
                    : $"Invalid state. Allowed: {string.Join(", ", TaskStates.All)}";
                results.Add(new BatchMoveItemResult
                {
                    JobId = item.JobId,
                    Status = "rejected",
                    Message = msg
                });
                continue;
            }

            MoveJobOutcome outcome;
            try
            {
                outcome = await MoveAsync(item.JobId, item.TargetState, item.WatchPath, ct, item.TargetIndex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch move item failed for {JobId} -> {Target}", item.JobId, item.TargetState);
                results.Add(new BatchMoveItemResult
                {
                    JobId = item.JobId,
                    Status = "failed",
                    Message = ex.Message
                });
                continue;
            }

            results.Add(outcome.Status switch
            {
                MoveJobStatus.Success =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "moved" },
                MoveJobStatus.NotFound =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "not-found" },
                MoveJobStatus.TargetFolderExists =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "conflict", Message = outcome.Message },
                _ =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "failed", Message = outcome.Message }
            });
        }
        return results;
    }

    /// <summary>
    /// Deterministic commit-attribution post-step. Gathers the commits in the
    /// task's run SHA-windows (plus the platform auto-commit), runs the pure
    /// <see cref="CommitAttributionService"/> rule engine, and persists the
    /// attributed chain + exclusions via <see cref="TaskMutationService"/>
    /// (never a direct file write). Best-effort: any failure is logged and
    /// swallowed so attribution never blocks the lane transition.
    /// </summary>
    private void RunCommitAttribution(TaskInfo moved, string? watchPath)
    {
        try
        {
            if (_sessions == null) return;

            // Pre-attribution candidate set: at this point moved.ExcludedCommits
            // is empty, so the aggregate the runner builds is the raw union of
            // run-range commits and the just-stamped auto-commit - exactly the
            // noisy input the rule engine is meant to clean up.
            var result = CommitAttributionRunner.Run(moved, watchPath, _sessions, _git);
            if (result == null) return;

            _mutations.SetCommitAttributionOnFolder(moved.FolderPath, result.Attributed, result.Excluded);
            _logger.LogInformation(
                "commit-attribution jobId={JobId} attributed={Attributed} excluded={Excluded}",
                moved.Id, result.Attributed.Count, result.Excluded.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "commit-attribution post-step failed for {JobId}", moved.Id);
        }
    }

    /// <summary>
    /// Fire-and-forget trigger for the opt-in drift post-steps (DRIFT Nachtrag).
    /// The transition has already committed on disk, so this runs on a detached
    /// task and swallows every failure: a drift run is an expensive extra pass
    /// whose outcome must never feed back into the lane move. The runner itself
    /// self-gates (default-OFF), so when no drift dimension is enabled for the
    /// project this returns almost immediately.
    /// </summary>
    private void TriggerDriftPostSteps(TaskInfo moved, ProjectSettings settings)
    {
        var runner = _driftRunner;
        if (runner == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(moved.ProjectName, moved.Id, moved.FolderPath, settings, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "drift post-step trigger failed for {JobId}", moved.Id);
            }
        });
    }

    private async Task<TaskCommitInfo?> TryAutoCommitAsync(string jobId, string? watchPath, CancellationToken ct)
    {
        try
        {
            // Pre-flight attribution guard: if every currently dirty path was last
            // modified before this task's first CLI run started, the working-tree
            // changes did not originate from this task's agent. Bundling them into
            // an auto-commit would stamp an unrelated SHA onto the job (see the
            // 2026-05-26 markdown task that ended up owning an AGENTS.md commit).
            // The guard is gated on TaskSessionLog so unit-test fixtures that
            // construct the service without it keep the legacy behavior.
            if (!IsWorkingTreeAttributableToTask(jobId, watchPath))
            {
                _logger.LogInformation(
                    "Auto-commit skipped for {JobId}: working-tree dirty paths predate the task's first run",
                    jobId);
                return null;
            }

            var (result, message) = await _git.AutoCommitAsync(jobId, watchPath, ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                _logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                return null;
            }

            var files = _git.GetCommitFiles(jobId, watchPath, result.Sha);
            return new TaskCommitInfo
            {
                Sha = result.Sha,
                ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                Message = message,
                FilesChanged = files.Count,
                Files = files.Select(f => f.Path).ToList(),
                At = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-commit threw for {JobId}. Moving without a recorded SHA.", jobId);
            return null;
        }
    }

    public async Task<int> PushCompletedJobCommitsAsync(TaskInfo job, string strategy, CancellationToken ct = default)
    {
        if (AutoPushStrategies.Normalize(strategy) == AutoPushStrategies.Never) return 0;
        if (job.State != TaskStates.Completed) return 0;

        var commits = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];

        var pushed = 0;
        foreach (var commit in commits.Where(c => !string.IsNullOrWhiteSpace(c.Sha)).OrderBy(c => c.At))
        {
            if (await TryPushCommitAsync(commit.Sha, job.WatchPath, job.Id, "completed", ct))
                pushed++;
        }
        return pushed;
    }

    /// <summary>
    /// Returns false when every currently dirty path in the project's working
    /// tree was last modified before this task's first recorded CLI run
    /// started - the signal that the changes were left over from another
    /// context (operator edit, an earlier task that never committed) rather
    /// than authored by this task's agent. Returns true (allow the auto-commit
    /// to proceed) in any of these cases:
    /// <list type="bullet">
    /// <item>No <see cref="TaskSessionLog"/> is wired (legacy fixtures).</item>
    /// <item>The task has no recorded session events yet (first-ever pickup,
    ///   no run history to compare against).</item>
    /// <item>The working tree is clean - <see cref="GitService.AutoCommitAsync"/>
    ///   will short-circuit on its own.</item>
    /// <item>At least one dirty path was modified at or after the task's first
    ///   run timestamp, or its mtime cannot be read (deleted, permission
    ///   error). When in doubt we defer to the auto-commit so a legitimately
    ///   edited file is never lost.</item>
    /// </list>
    /// </summary>
    private bool IsWorkingTreeAttributableToTask(string jobId, string? watchPath)
    {
        if (_sessions == null) return true;

        List<SessionEvent> events;
        try { events = _sessions.ReadSessionEvents(jobId, watchPath); }
        catch { return true; }
        if (events.Count == 0) return true;

        var firstActivityUtc = events.Min(e => e.Ts);
        if (firstActivityUtc == default) return true;

        var status = _git.GetStatus(jobId, watchPath);
        if (!status.IsRepo || status.Files.Count == 0) return true;

        var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return true;

        foreach (var file in status.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var fullPath = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            DateTime mtime;
            try
            {
                if (!File.Exists(fullPath))
                {
                    // Deletion or rename - we cannot timestamp it. Treat as
                    // potentially attributable so a real agent-driven delete
                    // is still committed.
                    return true;
                }
                mtime = File.GetLastWriteTimeUtc(fullPath);
            }
            catch
            {
                return true;
            }
            if (mtime >= firstActivityUtc) return true;
        }

        return false;
    }

    private async Task<bool> TryPushCommitAsync(string sha, string watchPath, string jobId, string reason, CancellationToken ct)
    {
        try
        {
            var result = await _git.PushShaAsync(sha, watchPath, ct);
            if (result.Success)
            {
                _logger.LogInformation("Auto-push {Status} for {JobId} at {Sha} ({Reason})", result.Status, jobId, sha, reason);
                return result.Status == "pushed";
            }

            _logger.LogWarning("Auto-push skipped for {JobId} at {Sha} ({Reason}): {Status} {Error}",
                jobId, sha, reason, result.Status, result.Error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-push threw for {JobId} at {Sha} ({Reason})", jobId, sha, reason);
            return false;
        }
    }
}
