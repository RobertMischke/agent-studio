using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
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
    private readonly OrchestratorApi.Services.Pipeline.MergeIntoDevelopRunner? _mergeRunner;
    private readonly CliRouter? _cliRouter;
    private readonly IAutoReviewPostProcessingQueue? _autoReviewQueue;
    private readonly TaskProvenanceService? _provenance;

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
        OrchestratorApi.Services.Drift.DriftPostStepRunner? driftRunner = null,
        CliRouter? cliRouter = null,
        IAutoReviewPostProcessingQueue? autoReviewQueue = null,
        OrchestratorApi.Services.Pipeline.MergeIntoDevelopRunner? mergeRunner = null,
        TaskProvenanceService? provenance = null)
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
        _cliRouter = cliRouter;
        _autoReviewQueue = autoReviewQueue;
        _mergeRunner = mergeRunner;
        _provenance = provenance;
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

        // Auto-commit is a per-project operator setting. Read it from the live
        // ProjectSettingsService cache for every transition so a UI toggle
        // affects the next move without a backend restart. TryAutoCommitAsync
        // still self-gates on a clean tree and scopes dirty paths to this task.
        var shouldAutoCommit =
            !isReadOnly &&
            info.State == TaskStates.Progress &&
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

        if (fromState == TaskStates.Escalated
            && targetState == TaskStates.Completed
            && !CanCompleteEscalatedJob(info, settings))
        {
            return new MoveJobOutcome(
                MoveJobStatus.Failure,
                $"Escalated tasks can only be accepted after their latest task commit is integrated into {settings.IntegrationBranch}.");
        }

        ReleaseCliOutputResourcesBeforeMove(info);
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
            && info.State == TaskStates.Progress)
        {
            var attributed = _scanner.FindJob(jobId, watchPath);
            if (attributed != null) EnterPostProcessingPhase(attributed);
            // Commit-attribution is a git step; skip it for read-only runs (they
            // produce no commits to attribute). Drift below is not a git step and
            // self-gates, so it still runs.
            if (attributed != null && !isReadOnly) RunCommitAttribution(attributed, watchPath);

            // DRIFT Nachtrag: fire the enabled drift dimensions as automatic
            // post-steps once the task has settled into auto-review. Fire-and-
            // forget and fully guarded - a drift failure (or the absence of any
            // enabled dimension; the runner self-gates default-OFF) must never
            // affect the lane transition that already completed above.
            if (attributed != null && _driftRunner != null && targetState == TaskStates.AutoReview)
            {
                TriggerDriftPostSteps(attributed, settings);
            }

            if (attributed != null && targetState == TaskStates.AutoReview)
            {
                EnqueueAutoReviewPostProcessing(attributed);
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
            // ASS-1724: the ONE commit-provenance recording hook. Anchor the
            // task/<id> tip + integration head at this lane crossing so the board
            // can graph "where does this work live" historically. Best-effort and
            // fully guarded inside the service - it runs after the move has landed,
            // so it can never undo the transition. Re-find post-move so the record
            // is written to the folder's new location with fresh provenance.
            if (_provenance != null)
            {
                var anchored = _scanner.FindJob(jobId, watchPath);
                if (anchored != null) _provenance.RecordTransition(anchored, targetState);
            }

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

            // Deferred "Merge into Develop" post-step. Accepting a done-green task
            // (the move into Completed) is the operator trigger that runs the real
            // task/<id> -> develop merge. Independent of the push strategy above:
            // a project that never auto-pushes still wants accepted work folded
            // into the integration branch. Fully guarded - the runner records a
            // visible conflict / error into the pipeline view but never throws, so
            // it cannot undo the lane move that already landed on disk.
            if (targetState == TaskStates.Completed && !isReadOnly && _mergeRunner != null)
            {
                var mergeJob = _scanner.FindJob(jobId, watchPath);
                if (mergeJob != null) TriggerMergeIntoDevelop(mergeJob, settings);
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

    private bool CanCompleteEscalatedJob(TaskInfo info, ProjectSettings settings)
    {
        // Read-only/no-code escalations have nothing to integrate; they can be
        // accepted once the operator has made the decision.
        if (TaskModes.IsReadOnly(info.Mode)) return true;
        if (info.Commits.Count == 0 && !info.CodeActivityDetected) return true;
        if (string.IsNullOrWhiteSpace(info.WatchPath) || !Directory.Exists(info.WatchPath)) return false;

        var latest = info.Commits.LastOrDefault()?.Sha ?? info.Commit?.Sha;
        if (string.IsNullOrWhiteSpace(latest)) return false;

        var integrationBranch = string.IsNullOrWhiteSpace(settings.IntegrationBranch)
            ? new ProjectSettings().IntegrationBranch
            : settings.IntegrationBranch;
        // The task's commits live in the project's CODE repository, which is not
        // necessarily WatchPath: in the dogfooding split WatchPath is the workspace
        // task-store (a separate git repo that does not contain the code SHA).
        // Passing WatchPath straight to IsAncestor checks the wrong repo, so the
        // ancestor probe always fails (rc=128, SHA absent) and NO escalated coding
        // task can ever be accepted. Resolve the real code repo root first.
        var repoRoot = _git.ResolveRepoRootForWatchPath(info.WatchPath) ?? info.WatchPath;
        return _git.IsAncestor(repoRoot, latest, integrationBranch);
    }

    private void EnterPostProcessingPhase(TaskInfo info)
    {
        _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);
        try
        {
            var now = DateTime.UtcNow;
            var snapshot = new LifecycleSnapshot
            {
                Phase = LifecyclePhases.PostProcessingRunning,
                PhaseEnteredAt = now,
                PostProcessingChecks =
                [
                    new LifecycleCheck
                    {
                        Name = "orchestrator-post-processing",
                        Status = "running",
                        StartedAt = now,
                        Detail = "Task execution finished; orchestrator post-processing is running before human review."
                    }
                ]
            };
            var path = Path.Combine(info.FolderPath, "lifecycle.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(snapshot, LifecycleJsonWriteOpts));
            PostProcessingOutcomeLog.Append(info.FolderPath, new PostProcessingOutcomeRecord
            {
                At = now,
                JobId = info.Id,
                Project = info.ProjectName,
                Outcome = PostProcessingOutcomes.FindingsAdded,
                Performer = PostProcessingPerformers.Orchestrator,
                StepId = OrchestratorApi.Services.Pipeline.PipelineCatalogue.GitCommitAttributionStepId,
                Summary = "Entered orchestrator post-processing after task execution.",
                EvidenceRef = "lifecycle.json"
            }, _logger);
            _logger.LogInformation(
                "post-processing-entered project={Project} job={JobId} performer={Performer}",
                info.ProjectName, info.Id, PostProcessingPerformers.Orchestrator);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write post-processing lifecycle evidence for {JobId}", info.Id);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions LifecycleJsonWriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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
                MoveJobStatus.DirectoryLocked =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "locked", Message = outcome.Message },
                _ =>
                    new BatchMoveItemResult { JobId = item.JobId, Status = "failed", Message = outcome.Message }
            });
        }
        return results;
    }

    private void ReleaseCliOutputResourcesBeforeMove(TaskInfo info)
    {
        if (_cliRouter == null) return;
        try
        {
            _cliRouter.Get(info.CliType).ReleaseOutputResources(info.TaskKey);
            _logger.LogDebug(
                "task-transition-released-cli-output jobId={JobId} cliType={CliType} target={TaskKey}",
                info.Id, info.CliType, info.TaskKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "task-transition-release-cli-output-failed jobId={JobId} cliType={CliType}",
                info.Id, info.CliType);
        }
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

            // Pre-attribution candidate set: the aggregate the runner builds is
            // the raw union of run-range commits and the just-stamped
            // auto-commit - exactly the noisy input the rule engine is meant to
            // clean up.
            var result = CommitAttributionRunner.Run(moved, watchPath, _sessions, _git);
            if (result == null) return;

            _mutations.SetCommitAttributionOnFolder(moved.FolderPath, result.Attributed);
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

    /// <summary>
    /// Triggers the deferred "Merge into Develop" post-step
    /// (<see cref="OrchestratorApi.Services.Pipeline.PipelineCatalogue.MergeIntoDevelopStepId"/>)
    /// on task acceptance. The runner performs the real
    /// <c>task/&lt;id&gt; -&gt; develop</c> merge and records the outcome into the
    /// pipeline view; it self-guards and never throws, so a conflict is made
    /// visible without affecting the lane move that already completed.
    /// </summary>
    private void TriggerMergeIntoDevelop(TaskInfo moved, ProjectSettings settings)
    {
        var runner = _mergeRunner;
        if (runner == null) return;
        try
        {
            runner.Run(moved.ProjectName, moved.Id, moved.FolderPath, moved.WatchPath, settings.IntegrationBranch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop trigger failed for {JobId}", moved.Id);
        }
    }

    private void EnqueueAutoReviewPostProcessing(TaskInfo moved)
    {
        var queue = _autoReviewQueue;
        if (queue == null) return;

        var accepted = queue.Enqueue(new AutoReviewPostProcessingRequest(
            ProjectName: moved.ProjectName,
            JobId: moved.Id,
            WatchPath: moved.WatchPath,
            EnqueuedAtUtc: DateTime.UtcNow,
            Source: "progress-to-auto-review"));

        if (accepted)
        {
            _logger.LogInformation(
                "auto-review-postprocessing-enqueued project={Project} job={JobId} source=progress-to-auto-review",
                moved.ProjectName, moved.Id);
        }
        else
        {
            _logger.LogWarning(
                "auto-review-postprocessing-enqueue-failed project={Project} job={JobId}",
                moved.ProjectName, moved.Id);
        }
    }

    private async Task<TaskCommitInfo?> TryAutoCommitAsync(string jobId, string? watchPath, CancellationToken ct)
    {
        try
        {
            // Pre-flight attribution scoping. In a sequential run (maxParallelism
            // ==1) the agent works in the SHARED main checkout, so the dirty tree
            // can mix this task's edits with leftover changes from an operator or
            // an earlier task that never committed. A blanket `git add -A` would
            // sweep all of them into this task's commit (the mega-blob / mis-
            // attribution bug). We classify each dirty path by mtime against the
            // task's first CLI run and commit only the ones authored during it.
            // Gated on TaskSessionLog so unit-test fixtures without it keep the
            // legacy whole-tree behavior.
            var plan = PlanAutoCommitScope(jobId, watchPath);
            if (plan.Scope == AutoCommitScope.None)
            {
                _logger.LogInformation(
                    "Auto-commit skipped for {JobId}: working-tree dirty paths predate the task's first run",
                    jobId);
                return null;
            }

            var pathspecs = plan.Scope == AutoCommitScope.Scoped ? plan.Paths : null;
            if (pathspecs != null)
                _logger.LogInformation(
                    "Auto-commit for {JobId}: scoping commit to {Count} task-attributable path(s); foreign dirty changes left untouched",
                    jobId, pathspecs.Count);

            var (result, message) = await _git.AutoCommitAsync(jobId, watchPath, ct, pathspecs);
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

    private enum AutoCommitScope
    {
        /// <summary>Commit the whole tree (legacy / unknown - no session window to scope against).</summary>
        All,
        /// <summary>Nothing is attributable to this task; skip the commit entirely.</summary>
        None,
        /// <summary>Commit only <see cref="AutoCommitPlan.Paths"/>.</summary>
        Scoped,
    }

    private sealed record AutoCommitPlan(AutoCommitScope Scope, IReadOnlyList<string> Paths);

    /// <summary>
    /// Classifies the project's dirty working tree against this task's first
    /// recorded CLI run so the auto-commit can stage only the task's own work.
    /// A dirty path is attributable when its last-write time is at or after the
    /// task's first session event (the agent touched it during the run) or when
    /// its timestamp cannot be read (deletion / rename / permission error - we
    /// defer to commit so a real agent-driven change is never lost). Paths whose
    /// mtime predates the run are leftover from another context (operator edit,
    /// an earlier task that never committed) and are excluded.
    ///
    /// <para>
    /// Returns <see cref="AutoCommitScope.All"/> (commit the whole tree, legacy
    /// <c>git add -A</c> path) when there is no window to scope against:
    /// <list type="bullet">
    /// <item>No <see cref="TaskSessionLog"/> is wired (legacy fixtures).</item>
    /// <item>The task has no recorded session events (first-ever pickup).</item>
    /// <item>The working tree is clean or the repo root can't be resolved -
    ///   <see cref="GitService.AutoCommitAsync"/> short-circuits on its own.</item>
    /// </list>
    /// Returns <see cref="AutoCommitScope.None"/> when every dirty path predates
    /// the run, and <see cref="AutoCommitScope.Scoped"/> with the attributable
    /// subset otherwise.
    /// </para>
    /// </summary>
    private AutoCommitPlan PlanAutoCommitScope(string jobId, string? watchPath)
    {
        var all = new AutoCommitPlan(AutoCommitScope.All, []);
        if (_sessions == null) return all;

        List<SessionEvent> events;
        try { events = _sessions.ReadSessionEvents(jobId, watchPath); }
        catch { return all; }
        if (events.Count == 0) return all;

        var firstActivityUtc = events.Min(e => e.Ts);
        if (firstActivityUtc == default) return all;

        var status = _git.GetStatus(jobId, watchPath);
        if (!status.IsRepo || status.Files.Count == 0) return all;

        var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return all;

        var scoped = new List<string>();
        foreach (var file in status.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var fullPath = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(fullPath))
                {
                    // Deletion or rename - we cannot timestamp it. Treat as
                    // attributable so a real agent-driven delete is still
                    // committed, but keep it scoped to this path; never widen
                    // back to a whole-tree `git add -A`.
                    scoped.Add(file.Path);
                    continue;
                }
                if (File.GetLastWriteTimeUtc(fullPath) >= firstActivityUtc)
                    scoped.Add(file.Path);
            }
            catch
            {
                // Unreadable mtime: defer to commit, but still scoped to the path.
                scoped.Add(file.Path);
            }
        }

        if (scoped.Count == 0) return new AutoCommitPlan(AutoCommitScope.None, []);
        return new AutoCommitPlan(AutoCommitScope.Scoped, scoped);
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
