namespace AgentStudio.ProjectGraph;

public static class ProjectGraphEndpoints
{
    public static void MapProjectGraphEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/graph", (
            string projectName,
            ProjectGraphDiscoveryService discovery) =>
        {
            var snapshot = discovery.GetCurrent(projectName);
            return snapshot is null
                ? Results.NotFound(new
                {
                    error = $"No persisted Project Graph capture is available for '{projectName}'.",
                    hint = "Run the project-map generator with --capture, or POST the explicit capture endpoint.",
                })
                : Results.Ok(snapshot);
        });

        app.MapPost("/api/projects/{projectName}/graph/captures", (
            string projectName,
            ProjectGraphDiscoveryService discovery) =>
        {
            var snapshot = discovery.Capture(projectName);
            return snapshot is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(snapshot);
        });
    }
}
