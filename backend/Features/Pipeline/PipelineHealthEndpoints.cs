namespace AgentStudio.Pipeline;

public static class PipelineHealthEndpoints
{
    public static void MapPipelineHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/pipeline-health", (
            string projectName,
            IPipelineHealthSensor health) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var snapshot = health.Snapshot(projectName);
            return snapshot is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(snapshot);
        });
    }
}
