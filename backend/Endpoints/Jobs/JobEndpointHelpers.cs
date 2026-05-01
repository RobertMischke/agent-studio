using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Internal helpers shared across the job endpoint groups: the
/// <see cref="MoveJobOutcome"/> to <see cref="IResult"/> translation and the
/// CLI-execution overlay applied to <see cref="JobInfo"/> and
/// <see cref="JobDetail"/> on read.
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

    internal static JobInfo WithExecution(JobInfo job, CliRouter router)
        => job with { Execution = router.Get(job.CliType).GetExecution(job.JobKey) };

    internal static JobDetail WithExecution(JobDetail detail, CliRouter router)
        => detail with { Info = WithExecution(detail.Info, router) };
}
