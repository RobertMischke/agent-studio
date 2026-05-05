using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Internal helpers shared across the job endpoint groups: the
/// <see cref="MoveJobOutcome"/> to <see cref="IResult"/> translation and the
/// runtime overlay (CLI execution + auto-loop snapshot) applied to
/// <see cref="JobInfo"/> and <see cref="JobDetail"/> on read.
/// </summary>
internal static class JobEndpointHelpers
{
    internal static IResult MoveResult(MoveJobOutcome outcome) => outcome.Status switch
    {
        MoveJobStatus.Success => Results.Ok(),
        MoveJobStatus.NotFound => Results.NotFound(),
        MoveJobStatus.TargetFolderExists => Results.Conflict(new { error = outcome.Message }),
        _ => Results.Json(new { error = outcome.Message ?? "Failed to move job" }, statusCode: StatusCodes.Status500InternalServerError)
    };

    internal static JobInfo WithRuntime(JobInfo job, CliRouter router, TaskRunnerService runners)
        => WithRuntime(job, router, runners, tokensByJobId: null);

    /// <summary>
    /// Overlay variant that also folds in per-job orchestrator token totals.
    /// The caller is expected to have read the orchestrator log once per
    /// watch path and built the lookup, so this stays O(1) per job — the
    /// perf contract locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>
    /// still holds.
    /// </summary>
    internal static JobInfo WithRuntime(
        JobInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, JobTokenSummary>? tokensByJobId)
    {
        var exec = router.Get(job.CliType).GetExecution(job.JobKey);
        // Look up auto-loop state by ProjectName (O(1) ConcurrentDictionary
        // hit) rather than by re-scanning all jobs from disk. Locked by
        // JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond.
        var loop = runners.GetStuckLoopStateForJob(job.Id, job.ProjectName);
        // The summarizer is fire-and-forget after a job lands in 4-review.
        // We surface its in-progress state on the JobInfo so the kanban
        // card can show "auto-reviewing" instead of looking idle while
        // the Haiku call is still working. Only return non-None states
        // so the field stays absent on cards where nothing is happening.
        var summary = runners.SummaryService.GetState(job.JobKey);
        JobTokenSummary? tokens = null;
        if (tokensByJobId != null && tokensByJobId.TryGetValue(job.JobKey, out var t) && t.TotalTokens > 0)
        {
            tokens = t;
        }
        return job with
        {
            Execution = exec,
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
            SummaryState = summary != null && summary.Status != JobSummaryStatus.None ? summary : null,
            TokenSummary = tokens
        };
    }

    /// <summary>
    /// Builds the per-watch-path → per-job token lookup used by
    /// <c>WithRuntime</c> in the listing endpoints. Reads each unique
    /// orchestrator log file at most once.
    /// </summary>
    internal static Dictionary<string, JobTokenSummary> BuildTokenLookup(
        IEnumerable<JobInfo> jobs,
        TokenSummaryService tokens)
    {
        // Read each watch path's orchestrator log at most once. Keyed by
        // JobKey (watchPath::jobId) so jobs that share an id across
        // watched workspaces stay distinct.
        var byWatchPath = new Dictionary<string, Dictionary<string, JobTokenSummary>>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, JobTokenSummary>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.WatchPath)) continue;
            if (!byWatchPath.TryGetValue(job.WatchPath, out var perJob))
            {
                perJob = tokens.SummarizePerJob(job.WatchPath);
                byWatchPath[job.WatchPath] = perJob;
            }
            if (perJob.TryGetValue(job.Id, out var t))
            {
                merged[job.JobKey] = t;
            }
        }
        return merged;
    }

    internal static JobDetail WithRuntime(JobDetail detail, CliRouter router, TaskRunnerService runners)
        => detail with { Info = WithRuntime(detail.Info, router, runners) };

    internal static JobDetail WithRuntime(
        JobDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, JobTokenSummary>? tokensByJobId)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId) };
}
