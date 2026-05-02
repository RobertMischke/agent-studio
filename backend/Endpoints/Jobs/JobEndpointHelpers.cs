using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;

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
    {
        var exec = router.Get(job.CliType).GetExecution(job.JobKey);
        var loop = runners.GetStuckLoopStateForJob(job.Id, job.WatchPath);
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
            }
        };
    }

    internal static JobDetail WithRuntime(JobDetail detail, CliRouter router, TaskRunnerService runners)
        => detail with { Info = WithRuntime(detail.Info, router, runners) };
}
