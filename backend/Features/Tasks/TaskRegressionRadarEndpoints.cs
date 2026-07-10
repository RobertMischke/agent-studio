using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

public static class TaskRegressionRadarEndpoints
{
    public static void MapTaskRegressionRadarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/regression-radar", (
            string jobId, string? project, string? watchPath,
            RegressionRadarService radar,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = radar.Analyze(jobId, watchPath);
            if (result.Error == "Job not found")
                return Results.NotFound(new { error = result.Error });
            return Results.Ok(result);
        });
    }
}
