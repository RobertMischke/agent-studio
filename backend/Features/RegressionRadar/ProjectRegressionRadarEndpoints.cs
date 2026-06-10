
namespace AgentStudio.RegressionRadar;

public static class ProjectRegressionRadarEndpoints
{
    public static void MapProjectRegressionRadarEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/regression-radar", (
            string projectName,
            RegressionRadarService radar) =>
        {
            var result = radar.AnalyzeProject(projectName);
            if (result.Error == "Project not found")
                return Results.NotFound(new { error = result.Error });
            return Results.Ok(result);
        });
    }
}
