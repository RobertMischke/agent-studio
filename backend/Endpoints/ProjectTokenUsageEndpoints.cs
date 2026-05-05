using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read surface for the project Token Usage panel (slice 8 of the
/// quality-system mockup, docs/mockups/quality-system/, "Token Usage"
/// surface). Four routes that all read the same project's
/// <c>orchestrator.jsonl</c> through <see cref="ProjectTokenUsageService"/>:
///
/// <list type="bullet">
///   <item>Summary: lifetime + last-24h totals plus the Job / Supporting
///   / Orchestrator category split (taxonomy.md vocabulary).</item>
///   <item>Heatmap: rows = jobs (most expensive first), columns = days.</item>
///   <item>Expensive jobs: top N jobs by total tokens.</item>
///   <item>Job detail: per-run breakdown with deltas for one job.</item>
/// </list>
///
/// Visibility, not enforcement (Critical Boundaries): these endpoints
/// only read the orchestrator log; no token totals influence scheduling
/// or job state.
/// </summary>
public static class ProjectTokenUsageEndpoints
{
    public static void MapProjectTokenUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/token-usage/summary", (
            string projectName,
            JobScannerService scanner,
            ProjectTokenUsageService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.BuildSummary(projectName, entry.Path));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/heatmap", (
            string projectName,
            int? days,
            JobScannerService scanner,
            ProjectTokenUsageService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.BuildHeatmap(projectName, entry.Path,
                days ?? ProjectTokenUsageService.DefaultHeatmapDays));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/expensive", (
            string projectName,
            int? limit,
            JobScannerService scanner,
            ProjectTokenUsageService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var list = svc.BuildExpensiveJobs(projectName, entry.Path,
                limit ?? ProjectTokenUsageService.DefaultExpensiveLimit);
            return Results.Ok(new { project = projectName, jobs = list });
        });

        app.MapGet("/api/projects/{projectName}/token-usage/job/{jobId}", (
            string projectName,
            string jobId,
            JobScannerService scanner,
            ProjectTokenUsageService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            if (string.IsNullOrWhiteSpace(jobId))
                return Results.BadRequest(new { error = "job id required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var detail = svc.BuildJobDetail(projectName, entry.Path, jobId);
            if (detail is null)
                return Results.NotFound(new { error = $"No token activity recorded for job '{jobId}'." });
            return Results.Ok(detail);
        });
    }
}
