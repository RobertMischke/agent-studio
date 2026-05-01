using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Internal helpers shared across the job endpoint groups:
/// the auto-commit-on-move flow, the <see cref="MoveJobOutcome"/> →
/// <see cref="IResult"/> translation, and the CLI-execution overlay
/// applied to <see cref="JobInfo"/>/<see cref="JobDetail"/> on read.
///
/// These intentionally live next to the job endpoint feature folder
/// rather than as a public service: they are presentation-layer
/// glue (HTTP shape decisions, response composition), not domain
/// logic. Keeping them out of <c>Services/</c> stops them from
/// being reused from places where the HTTP context isn't right.
/// </summary>
internal static class JobEndpointHelpers
{
    /// <summary>
    /// Wraps <see cref="JobScannerService.MoveJob"/> with the auto-commit hook:
    /// when the project has auto-commit enabled and the transition is
    /// <c>3-progress → 4-review</c>, generate a Conventional Commit message via
    /// Haiku, commit on the workspace repo, then move the job folder and stamp
    /// the SHA onto its <c>job.json</c>. The move always proceeds — a commit
    /// failure is logged but never blocks the state transition, so the user
    /// never gets stuck mid-pipeline because the LLM call timed out.
    /// </summary>
    internal static async Task<MoveJobOutcome> MoveAndMaybeAutoCommitAsync(
        JobScannerService scanner, JobStateMachine states, JobMutationService mutations,
        GitService git, ProjectSettingsService settings, ILogger logger,
        string jobId, string targetState, string? watchPath, CancellationToken ct)
    {
        var info = scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);

        var shouldAutoCommit =
            info.State == JobStates.Progress &&
            targetState == JobStates.Review &&
            settings.Get(info.ProjectName).AutoCommit;

        JobCommitInfo? commitToStamp = null;
        if (shouldAutoCommit)
        {
            try
            {
                var (result, message) = await git.AutoCommitAsync(jobId, watchPath, ct);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Sha))
                {
                    var files = git.GetCommitFiles(jobId, watchPath, result.Sha);
                    commitToStamp = new JobCommitInfo
                    {
                        Sha = result.Sha,
                        ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                        Message = message,
                        FilesChanged = files.Count,
                        Files = files.Select(f => f.Path).ToList(),
                        At = DateTime.UtcNow
                    };
                }
                else
                {
                    logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-commit threw for {JobId} — moving without a recorded SHA", jobId);
            }
        }

        var outcome = states.MoveJob(jobId, targetState, watchPath);
        if (outcome.Status == MoveJobStatus.Success && commitToStamp != null)
        {
            // Re-resolve the job — its FolderPath has shifted from progress/ to review/.
            var moved = scanner.FindJob(jobId, watchPath);
            if (moved != null)
                mutations.SetJobCommitOnFolder(moved.FolderPath, commitToStamp);
        }

        return outcome;
    }

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
