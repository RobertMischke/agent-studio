using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.RegressionRadar;

namespace OrchestratorApi.Endpoints.Jobs;

public static class JobRegressionRadarEndpoints
{
    public static void MapJobRegressionRadarEndpoints(this RouteGroupBuilder group)
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
