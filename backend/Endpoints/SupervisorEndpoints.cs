using Microsoft.AspNetCore.Mvc;
using OrchestratorApi.Services.Supervisor;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// HTTP surface for the per-project supervisor. Read-only in this first cut:
/// only the observation primitive is wired. Emergency primitives and
/// advisories land in subsequent supervisor tasks.
/// </summary>
public static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/supervisor");

        group.MapGet("/{project}/observation", async (
            string project,
            ProjectObservationService obs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var observation = await obs.ObserveAsync(project, ct);
            return Results.Ok(observation);
        });
    }
}
