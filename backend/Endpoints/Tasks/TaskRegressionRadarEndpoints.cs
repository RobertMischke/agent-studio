using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.RegressionRadar;

namespace OrchestratorApi.Endpoints.Tasks;

public static class TaskRegressionRadarEndpoints
{
    public static void MapTaskRegressionRadarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/regression-radar", (
            string jobId, string? watchPath,
            RegressionRadarService radar) =>
        {
            var result = radar.Analyze(jobId, watchPath);
            if (result.Error == "Job not found")
                return Results.NotFound(new { error = result.Error });
            return Results.Ok(result);
        });
    }
}
