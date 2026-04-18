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
                Preparation = jobs.Where(j => j.State == JobStates.Preparation).ToList(),
                Ready = jobs.Where(j => j.State == JobStates.Ready).ToList(),
                Progress = jobs.Where(j => j.State == JobStates.Progress).ToList(),
                Review = jobs.Where(j => j.State == JobStates.Review).ToList(),
                Completed = jobs.Where(j => j.State == JobStates.Completed).ToList()
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

        app.MapGet("/api/watch-paths", (JobScannerService scanner) =>
        {
            return Results.Ok(scanner.GetWatchPaths());
        });

        app.MapGet("/healthz", () => Results.Ok("ok"));
    }
}
