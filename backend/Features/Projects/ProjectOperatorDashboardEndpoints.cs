namespace AgentStudio.Projects;

public static class ProjectOperatorDashboardEndpoints
{
    public static void MapProjectOperatorDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/throughput", (
            string projectName,
            ProjectThroughputService throughput) =>
        {
            var summary = throughput.Build(projectName);
            return summary is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(summary);
        });

        app.MapGet("/api/projects/{projectName}/deployment/summary", (
            string projectName,
            ProjectDeploymentSummaryService deployments) =>
        {
            var summary = deployments.Build(projectName);
            return summary is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(summary);
        });

        app.MapGet("/api/projects/{projectName}/visual-evidence", (
            string projectName,
            ProjectVisualEvidenceService evidence) =>
        {
            var queue = evidence.Build(projectName);
            return queue is null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(queue);
        });

        app.MapPost("/api/projects/{projectName}/visual-evidence/{itemId}/acknowledge", (
            string projectName,
            string itemId,
            ProjectVisualEvidenceService evidence) =>
        {
            var item = evidence.Acknowledge(projectName, itemId);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
    }
}
