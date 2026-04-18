using OrchestratorApi.Models;
using OrchestratorApi.Services;

namespace OrchestratorApi.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", (JobScannerService scanner) =>
        {
            var jobs = scanner.ScanAllJobs();
            return Results.Ok(jobs);
        });

        group.MapGet("/grouped", (JobScannerService scanner) =>
        {
            var jobs = scanner.ScanAllJobs();
            var grouped = new
            {
                Preparation = jobs.Where(j => j.State == JobStates.Preparation).OrderBy(j => j.Order).ToList(),
                Ready = jobs.Where(j => j.State == JobStates.Ready).OrderBy(j => j.Order).ToList(),
                Progress = jobs.Where(j => j.State == JobStates.Progress).OrderBy(j => j.Order).ToList(),
                Review = jobs.Where(j => j.State == JobStates.Review).OrderBy(j => j.Order).ToList(),
                Completed = jobs.Where(j => j.State == JobStates.Completed).OrderBy(j => j.Order).ToList()
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, JobScannerService scanner) =>
        {
            var detail = scanner.GetJobDetail(jobId);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/{jobId}/state", (string jobId, MoveJobRequest req, JobScannerService scanner) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            var success = scanner.MoveJob(jobId, req.TargetState);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{jobId}/move", (string jobId, MoveJobRequest req, JobScannerService scanner) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            var success = scanner.MoveJob(jobId, req.TargetState);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("/{jobId}/files/{fileName}", (string jobId, string fileName, JobScannerService scanner) =>
        {
            var content = scanner.ReadJobFile(jobId, fileName);
            return content is null ? Results.NotFound() : Results.Text(content);
        });

        group.MapPost("/", (CreateJobRequest req, JobScannerService scanner) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");

            var jobId = scanner.CreateJob(req);
            return jobId is null ? Results.Conflict("Job already exists or invalid input") : Results.Ok(new { id = jobId });
        });

        group.MapPut("/{jobId}/files/{fileName}", (string jobId, string fileName, UpdateJobFileRequest req, JobScannerService scanner) =>
        {
            var success = scanner.UpdateJobFile(jobId, fileName, req.Content);
            return success ? Results.Ok() : Results.BadRequest("Cannot edit (job in progress or not found)");
        });

        group.MapPost("/reorder", (ReorderRequest req, JobScannerService scanner) =>
        {
            var success = scanner.ReorderJobs(req.JobIds);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        app.MapGet("/api/watch-paths", (JobScannerService scanner) =>
        {
            var entries = scanner.GetWatchPaths();
            return Results.Ok(entries);
        });

        app.MapGet("/healthz", () => Results.Ok("ok"));
    }
}
