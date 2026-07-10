

namespace AgentStudio.Tasks;

/// <summary>
/// Internal helpers shared across the job endpoint groups: the
/// <see cref="MoveJobOutcome"/> to <see cref="IResult"/> translation and the
/// runtime overlay (CLI execution + auto-loop snapshot) applied to
/// <see cref="TaskInfo"/> and <see cref="TaskDetail"/> on read.
/// </summary>
internal static class TaskEndpointHelpers
{
    internal static IResult MoveResult(MoveJobOutcome outcome) => outcome.Status switch
    {
        MoveJobStatus.Success => Results.Ok(),
        MoveJobStatus.NotFound => Results.NotFound(),
        MoveJobStatus.TargetFolderExists => Results.Conflict(new { error = outcome.Message }),
        MoveJobStatus.DirectoryLocked => Results.Json(
            new { error = outcome.Message ?? "Task folder is temporarily locked by another process. Retry after the active process releases its file handles." },
            statusCode: StatusCodes.Status423Locked),
        _ => Results.Json(new { error = outcome.Message ?? "Failed to move job" }, statusCode: StatusCodes.Status500InternalServerError)
    };

    internal static TaskInfo WithRuntime(TaskInfo job, CliRouter router, TaskRunnerService runners)
        => WithRuntime(job, router, runners, tokensByJobId: null, verdictsByJobKey: null);

    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId)
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey: null, waitsOnByJobKey: null);

    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey: null);

    /// <summary>
    /// Overlay variant that also folds in per-job orchestrator token totals,
    /// the latest orchestrator-review verdict, and the AGT-2029 waits-on
    /// status. The caller is expected to have built the lookups once per
    /// request, so this stays O(1) per job — the perf contract locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>
    /// still holds.
    /// </summary>
    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey,
        IReadOnlyDictionary<string, WaitsOnStatus>? waitsOnByJobKey)
    {
        // Lane is the single source of truth for "is this card live". A job
        // outside 3-progress has finished or been moved on; surfacing a stale
        // (or even live) Execution snapshot here lets a "running" status leak
        // onto cards in 4-auto-review / 5-human-review / 6-completed, which
        // the per-card pill then renders as a misleading "Running" badge.
        // Clearing at the wire-overlay layer keeps Lane > Execution-Status
        // > Default as the deterministic precedence for every consumer.
        var exec = job.State == TaskStates.Progress
            ? router.Get(job.CliType).GetExecution(job.TaskKey)
            : null;
        // Look up auto-loop state by ProjectName (O(1) ConcurrentDictionary
        // hit) rather than by re-scanning all jobs from disk. Locked by
        // JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond.
        var loop = runners.GetStuckLoopStateForJob(job.Id, job.ProjectName);
        // The summarizer is fire-and-forget after a job lands in 4-review.
        // We surface its in-progress state on the TaskInfo so the kanban
        // card can show "auto-reviewing" instead of looking idle while
        // the Haiku call is still working. Only return non-None states
        // so the field stays absent on cards where nothing is happening.
        var summary = runners.SummaryService.GetState(job.TaskKey);
        TaskTokenSummary? tokens = null;
        if (tokensByJobId != null && tokensByJobId.TryGetValue(job.TaskKey, out var t) && t.TotalTokens > 0)
        {
            tokens = TokenSummaryService.WithModelFallback(t, exec?.Model ?? job.Model);
        }
        string? verdict = null;
        if (verdictsByJobKey != null && verdictsByJobKey.TryGetValue(job.TaskKey, out var v))
        {
            verdict = v;
        }
        // AGT-2029: fold in the pre-computed waits-on status (which dependsOn
        // targets are fulfilled / open, blocked, or on a cycle) so the kanban
        // card can render a state-aware, navigable dependency chip. Only cards
        // with dependsOn edges get an entry; the rest carry null.
        WaitsOnStatus? waitsOn = null;
        if (waitsOnByJobKey != null && waitsOnByJobKey.TryGetValue(job.TaskKey, out var w))
        {
            waitsOn = w;
        }
        // Reconcile a stale Warn-class outcome chip against the final verdict: an
        // accepted card must not surface a classifier-unknown/heuristic-done/
        // missing-terminal-sentinel chip that contradicts its accept (ASS-775).
        // The scanner already clears this when the accept note is in the log; this
        // covers 5-human-review accepts whose accept note never reached the log.
        var outcomeIssue = TaskOutcomeIssueReconciliation.ShouldSuppress(
            job.OutcomeIssue, verdictAccepted: string.Equals(verdict, "accept", StringComparison.Ordinal))
            ? null
            : job.OutcomeIssue;
        // ASS-1751: classify why a 3-progress card looks "untouched" — a live
        // run, a failed run waiting out the rapid-crash backoff, or an orphan
        // killed by a backend restart. Gated on the Progress lane (same O(1)
        // runner lookup as the auto-loop snapshot above) so the perf contract
        // holds; pure visibility, no behavior. Null on every other lane.
        TaskRunActivity? runActivity = job.State == TaskStates.Progress
            ? TaskRunActivityClassifier.Classify(
                runners.GetRunActivityForJob(job.Id, job.ProjectName),
                exec,
                outcomeIssue,
                DateTime.UtcNow)
            : null;
        // AGT-2003: project the active run-lease owner onto the card while the
        // task is in-progress. Same Progress-lane gate + O(1) in-memory peek as
        // RunActivity above, so the JobsEndpointPerfTests contract still holds. A
        // remote runner acquires this lease; a plain local in-process run holds
        // none, so this stays null and the card shows the quiet local presentation.
        TaskRunnerInfo? runner = job.State == TaskStates.Progress
            ? runners.ResolveRunnerBadge(job.TaskKey)
            : null;
        return job with
        {
            Execution = exec,
            OutcomeIssue = outcomeIssue,
            RunActivity = runActivity,
            Runner = runner,
            AutoLoop = loop == null ? null : new AutoLoopSnapshot
            {
                Iteration = loop.IterationCount,
                MaxIterations = runners.StuckLoopBudget.MaxIterations,
                TokensUsed = loop.CumulativeOrchestratorTokens,
                MaxTokens = runners.StuckLoopBudget.MaxOrchestratorTokens,
                StartedAt = loop.FirstAt,
                LastAt = loop.LastAt,
                LastQuestion = loop.LastQuestion,
                LastReply = loop.LastReply,
                LastError = loop.LastError
            },
            SummaryState = summary != null && summary.Status != TaskSummaryStatus.None ? summary : null,
            TokenSummary = tokens,
            OrchestratorVerdict = verdict,
            WaitsOn = waitsOn
        };
    }

    /// <summary>
    /// AGT-2046 — folds the batched board merge signal onto each job. The lookup
    /// is built ONCE per request by <see cref="BoardMergeStatusService"/> (O(repos)
    /// git spawns, never per card), so this stays an O(1) dictionary hit per job,
    /// preserving the <c>JobsEndpointPerfTests</c> contract. Jobs without a signal
    /// (no committed/merged anchor) are passed through untouched.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithMergeSignal(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskMergeSignal> mergeByJobKey)
        => jobs.Select(job =>
            mergeByJobKey.TryGetValue(job.TaskKey, out var signal)
                ? job with { MergeSignal = signal }
                : job);

    /// <summary>
    /// AGT-2029 — builds a per-TaskKey lookup of waits-on status for the jobs
    /// that actually carry dependsOn edges. Resolution is <b>archive-inclusive</b>
    /// (a dependency is fulfilled when its target reaches 6-completed OR
    /// 7-archive, and the board snapshot omits archive), so the key index is
    /// built from <see cref="TaskScannerService.ScanAllJobsWithArchive"/> once
    /// per request. Cards without dependencies are skipped, keeping the common
    /// case free.
    /// </summary>
    internal static Dictionary<string, WaitsOnStatus> BuildWaitsOnLookup(
        IEnumerable<TaskInfo> jobs,
        TaskScannerService scanner)
    {
        var result = new Dictionary<string, WaitsOnStatus>(StringComparer.Ordinal);
        var withDeps = jobs.Where(j => j.References?.DependsOn.Count > 0).ToList();
        if (withDeps.Count == 0) return result;

        // One archive-inclusive index for the whole request; keys are globally
        // unique across projects, so this resolves cross-project targets too.
        var index = TaskReferenceIndex.Build(scanner.ScanAllJobsWithArchive());
        foreach (var job in withDeps)
        {
            result[job.TaskKey] = index.EvaluateWaitsOn(job);
        }
        return result;
    }

    /// <summary>
    /// Builds a per-TaskKey lookup of the latest orchestrator-review
    /// verdict, sourced from <see cref="ReviewDecisionLog"/>. One JSONL
    /// read per (workspace, project) pair so the per-job overlay stays
    /// O(1). Maps <see cref="ReviewDecisionKind"/> to the wire enum
    /// (<c>reissue</c> / <c>escalate</c> / <c>accept</c>); skipped
    /// records do not surface a verdict.
    /// </summary>
    internal static Dictionary<string, string> BuildOrchestratorVerdictLookup(
        IEnumerable<TaskInfo> jobs,
        IConfiguration configuration)
    {
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspace = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return verdicts;

        // Read each (workspace, project) journal at most once per request.
        var byProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;
            if (!byProject.TryGetValue(job.ProjectName, out var records))
            {
                try { records = ReviewDecisionLog.ReadAll(workspace!, job.ProjectName); }
                catch { records = Array.Empty<ReviewDecisionRecord>(); }
                byProject[job.ProjectName] = records;
            }
            // Latest record wins. The journal is append-only; the last
            // entry for this jobId reflects the most recent decision.
            ReviewDecisionRecord? latest = null;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].JobId == job.Id)
                {
                    latest = records[i];
                    break;
                }
            }
            if (latest == null) continue;
            var verdict = latest.Kind switch
            {
                ReviewDecisionKind.Reissue      => "reissue",
                ReviewDecisionKind.Escalate     => "escalate",
                ReviewDecisionKind.AcceptAsDone => "accept",
                _                                => (string?)null
            };
            if (verdict != null) verdicts[job.TaskKey] = verdict;
        }
        return verdicts;
    }

    /// <summary>
    /// Builds the per-project → per-job token lookup used by
    /// <c>WithRuntime</c> in the listing endpoints. Reads each unique
    /// project bus projection at most once.
    /// </summary>
    internal static Dictionary<string, TaskTokenSummary> BuildTokenLookup(
        IEnumerable<TaskInfo> jobs,
        ITokenAggregator tokens)
    {
        // Read each project projection at most once. Keyed by TaskKey
        // (watchPath::jobId) so jobs that share an id across watched
        // workspaces stay distinct.
        var byProject = new Dictionary<string, Dictionary<string, TaskTokenSummary>>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.WatchPath)) continue;
            var projectKey = string.IsNullOrWhiteSpace(job.ProjectName)
                ? job.WatchPath
                : $"{job.ProjectName}\n{job.WatchPath}";
            if (!byProject.TryGetValue(projectKey, out var perJob))
            {
                perJob = tokens.WorkspacePerJob(job.ProjectName, job.WatchPath);
                byProject[projectKey] = perJob;
            }
            if (perJob.TryGetValue(job.Id, out var t))
            {
                merged[job.TaskKey] = t;
            }
        }
        return merged;
    }

    internal static TaskDetail WithRuntime(TaskDetail detail, CliRouter router, TaskRunnerService runners)
        => detail with { Info = WithRuntime(detail.Info, router, runners) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey: null) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey,
        IReadOnlyDictionary<string, WaitsOnStatus>? waitsOnByJobKey)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey) };
}
