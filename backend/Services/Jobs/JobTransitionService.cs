using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Application-owned job state transitions that need side effects around the
/// raw folder move. Manual API moves and automatic runner completion both use
/// this service so lifecycle policy stays in code, not in agent prompts.
/// </summary>
public sealed class JobTransitionService
{
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly JobMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<JobTransitionService> _logger;

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

    public JobTransitionService(
        JobScannerService scanner,
        JobStateMachine states,
        JobMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<JobTransitionService> logger)
    {
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
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

        var shouldAutoCommit =
            info.State == JobStates.Progress &&
            targetState == JobStates.AutoReview &&
            settings.AutoCommit;

        JobCommitInfo? commitToStamp = null;
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
            if (targetState == JobStates.Completed && autoPushStrategy != AutoPushStrategies.Never)
            {
                var moved = _scanner.FindJob(jobId, watchPath);
                if (moved != null)
                {
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
    /// endpoint backing <c>POST /api/jobs/batch-move</c> and exists so the
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

            if (!JobStates.All.Contains(item.TargetState))
            {
                var msg = JobStates.NumberedLegacyMap.TryGetValue(item.TargetState, out var renamed)
                    ? $"Lane '{item.TargetState}' was renamed in ADR-0025. Use '{renamed}' instead."
                    : $"Invalid state. Allowed: {string.Join(", ", JobStates.All)}";
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

    private async Task<JobCommitInfo?> TryAutoCommitAsync(string jobId, string? watchPath, CancellationToken ct)
    {
        try
        {
            var (result, message) = await _git.AutoCommitAsync(jobId, watchPath, ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                _logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                return null;
            }

            var files = _git.GetCommitFiles(jobId, watchPath, result.Sha);
            return new JobCommitInfo
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

    public async Task<int> PushCompletedJobCommitsAsync(JobInfo job, string strategy, CancellationToken ct = default)
    {
        if (AutoPushStrategies.Normalize(strategy) == AutoPushStrategies.Never) return 0;
        if (job.State != JobStates.Completed) return 0;

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
