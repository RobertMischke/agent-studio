namespace AgentStudio.Projects;

/// <summary>
/// <c>GET /api/projects/{projectName}/cycle-time?window=7d|30d|all</c>:
/// per-stage cycle-time aggregates (count, median, p90, max) plus the per-task
/// rows behind them. The project handle accepts the watch-path name, the
/// registry id (<c>PROJ-NNN</c>), or the short code.
/// </summary>
public static class ProjectCycleTimeEndpoints
{
    public static void MapProjectCycleTimeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/cycle-time", (
            string projectName,
            string? window,
            ProjectCycleTimeService cycleTime) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            if (!ProjectCycleTimeService.TryParseWindow(window, out _, out _))
                return Results.BadRequest(new { error = $"Invalid window '{window}'. Use 7d, 30d, or all." });

            var response = cycleTime.Build(projectName, window);
            return response is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(response);
        });
    }
}
