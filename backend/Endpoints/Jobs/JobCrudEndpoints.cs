using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using static OrchestratorApi.Endpoints.Jobs.JobEndpointHelpers;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Job CRUD + state transitions: list, detail, create, delete, move,
/// reorder, change-project, plus the "set one job field" PUTs (model,
/// cli-type, title). These are the routes that read or rewrite the
/// canonical <c>job.json</c> on disk.
/// </summary>
public static class JobCrudEndpoints
{
    public static void MapJobCrudEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", (JobScannerService scanner, CliRouter router) =>
        {
            var jobs = scanner.ScanAllJobs().Select(job => WithExecution(job, router)).ToList();
            return Results.Ok(jobs);
        });

        group.MapGet("/grouped", (JobScannerService scanner, CliRouter router) =>
        {
            var jobs = scanner.ScanAllJobs().Select(job => WithExecution(job, router)).ToList();
            var grouped = new
            {
                Preparation = jobs.Where(j => j.State == JobStates.Preparation).OrderBy(j => j.Order).ToList(),
                Ready = jobs.Where(j => j.State == JobStates.Ready).OrderBy(j => j.Order).ToList(),
                Progress = jobs.Where(j => j.State == JobStates.Progress).OrderBy(j => j.Order).ToList(),
                Review = jobs.Where(j => j.State == JobStates.Review).OrderBy(j => j.Order).ToList(),
                Completed = jobs.Where(j => j.State == JobStates.Completed).OrderBy(j => j.Order).ToList(),
                Archive = jobs.Where(j => j.State == JobStates.Archive).OrderBy(j => j.Order).ToList()
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, string? watchPath, JobScannerService scanner, CliRouter router) =>
        {
            var detail = scanner.GetJobDetail(jobId, watchPath);
            return detail is null ? Results.NotFound() : Results.Ok(WithExecution(detail, router));
        });

        group.MapPut("/{jobId}/state", async (string jobId, string? watchPath, MoveJobRequest req,
            JobScannerService scanner, GitService git, ProjectSettingsService settings, ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            return MoveResult(await MoveAndMaybeAutoCommitAsync(scanner, git, settings, logger, jobId, req.TargetState, watchPath, ct));
        });

        group.MapPost("/{jobId}/move", async (string jobId, string? watchPath, MoveJobRequest req,
            JobScannerService scanner, GitService git, ProjectSettingsService settings, ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            return MoveResult(await MoveAndMaybeAutoCommitAsync(scanner, git, settings, logger, jobId, req.TargetState, watchPath, ct));
        });

        group.MapDelete("/{jobId}", (string jobId, string? watchPath, JobScannerService scanner) =>
        {
            var success = scanner.DeleteJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/", (CreateJobRequest req, JobScannerService scanner) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");

            var jobId = scanner.CreateJob(req);
            return jobId is null ? Results.Conflict("Job already exists or invalid input") : Results.Ok(new { id = jobId });
        });

        group.MapPost("/reorder", (ReorderRequest req, JobScannerService scanner) =>
        {
            var jobs = req.Jobs.Count > 0
                ? req.Jobs
                : req.JobIds.Select(id => new JobOrderItem { JobId = id }).ToList();
            var success = scanner.ReorderJobs(jobs);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        group.MapPost("/{jobId}/change-project", (string jobId, string? watchPath, ChangeProjectRequest req, JobScannerService scanner) =>
        {
            var success = scanner.ChangeProject(jobId, req.TargetWatchPath, watchPath);
            return success ? Results.Ok() : Results.BadRequest("Failed to change project");
        });

        group.MapPut("/{jobId}/model", (string jobId, string? watchPath, SetJobModelRequest req, JobScannerService scanner) =>
        {
            var success = scanner.SetJobModel(jobId, req?.Model, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/cli-type", (string jobId, string? watchPath, SetJobCliTypeRequest req, JobScannerService scanner) =>
        {
            if (req is null || !CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"cliType must be one of {string.Join(", ", CliTypes.All)}" });
            var ok = scanner.SetJobCliType(jobId, req.CliType, watchPath);
            if (!ok) return Results.NotFound();
            if (req.UseOwnSession.HasValue)
                scanner.SetJobUseOwnSession(jobId, req.UseOwnSession.Value, watchPath);
            return Results.Ok();
        });

        group.MapPut("/{jobId}/title", (string jobId, string? watchPath, SetJobTitleRequest req, JobScannerService scanner) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var success = scanner.SetJobTitle(jobId, req.Title, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });
    }
}
