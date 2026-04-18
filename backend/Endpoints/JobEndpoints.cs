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
                Active = jobs.Where(j => JobStates.Categorize(j.State) == "active").ToList(),
                Review = jobs.Where(j => JobStates.Categorize(j.State) == "review").ToList(),
                Completed = jobs.Where(j => JobStates.Categorize(j.State) == "completed").ToList(),
                Failed = jobs.Where(j => JobStates.Categorize(j.State) == "failed").ToList(),
                Idle = jobs.Where(j => JobStates.Categorize(j.State) == "idle").ToList()
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, JobScannerService scanner) =>
        {
            var detail = scanner.GetJobDetail(jobId);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/{jobId}/state", (string jobId, UpdateStateRequest req, JobScannerService scanner) =>
        {
            if (!JobStates.All.Contains(req.State))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            var success = scanner.UpdateJobState(jobId, req.State);
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
