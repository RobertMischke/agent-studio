using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;

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
        => WithRuntime(job, router, runners, tokensByJobId: null, verdictsByJobKey: null);

    internal static JobInfo WithRuntime(
        JobInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, JobTokenSummary>? tokensByJobId)
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey: null);

    /// <summary>
    /// Overlay variant that also folds in per-job orchestrator token totals
    /// and the latest orchestrator-review verdict. The caller is expected
    /// to have built both lookups once per request, so this stays O(1) per
    /// job — the perf contract locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>
    /// still holds.
    /// </summary>
    internal static JobInfo WithRuntime(
        JobInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, JobTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
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
        string? verdict = null;
        if (verdictsByJobKey != null && verdictsByJobKey.TryGetValue(job.JobKey, out var v))
        {
            verdict = v;
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
            TokenSummary = tokens,
            OrchestratorVerdict = verdict
        };
    }

    /// <summary>
    /// Builds a per-JobKey lookup of the latest orchestrator-review
    /// verdict, sourced from <see cref="ReviewDecisionLog"/>. One JSONL
    /// read per (workspace, project) pair so the per-job overlay stays
    /// O(1). Maps <see cref="ReviewDecisionKind"/> to the wire enum
    /// (<c>reissue</c> / <c>escalate</c> / <c>accept</c>); skipped
    /// records do not surface a verdict.
    /// </summary>
    internal static Dictionary<string, string> BuildOrchestratorVerdictLookup(
        IEnumerable<JobInfo> jobs,
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
            if (verdict != null) verdicts[job.JobKey] = verdict;
        }
        return verdicts;
    }

    /// <summary>
    /// Builds the per-project → per-job token lookup used by
    /// <c>WithRuntime</c> in the listing endpoints. Reads each unique
    /// project bus projection at most once.
    /// </summary>
    internal static Dictionary<string, JobTokenSummary> BuildTokenLookup(
        IEnumerable<JobInfo> jobs,
        ITokenAggregator tokens)
    {
        // Read each project projection at most once. Keyed by JobKey
        // (watchPath::jobId) so jobs that share an id across watched
        // workspaces stay distinct.
        var byProject = new Dictionary<string, Dictionary<string, JobTokenSummary>>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, JobTokenSummary>(StringComparer.Ordinal);
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

    internal static JobDetail WithRuntime(
        JobDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, JobTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey) };
}
