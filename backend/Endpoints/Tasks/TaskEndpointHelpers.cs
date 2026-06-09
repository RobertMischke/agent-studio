using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;

namespace OrchestratorApi.Endpoints.Tasks;

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
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey: null);

    /// <summary>
    /// Overlay variant that also folds in per-job orchestrator token totals
    /// and the latest orchestrator-review verdict. The caller is expected
    /// to have built both lookups once per request, so this stays O(1) per
    /// job — the perf contract locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>
    /// still holds.
    /// </summary>
    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
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
        // Reconcile a stale Warn-class outcome chip against the final verdict: an
        // accepted card must not surface a classifier-unknown/heuristic-done/
        // missing-terminal-sentinel chip that contradicts its accept (ASS-775).
        // The scanner already clears this when the accept note is in the log; this
        // covers 5-human-review accepts whose accept note never reached the log.
        var outcomeIssue = TaskOutcomeIssueReconciliation.ShouldSuppress(
            job.OutcomeIssue, verdictAccepted: string.Equals(verdict, "accept", StringComparison.Ordinal))
            ? null
            : job.OutcomeIssue;
        return job with
        {
            Execution = exec,
            OutcomeIssue = outcomeIssue,
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
            OrchestratorVerdict = verdict
        };
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
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey) };
}
