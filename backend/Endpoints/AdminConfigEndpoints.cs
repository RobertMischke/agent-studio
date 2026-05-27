using System.Text.Json;
using OrchestratorApi.Services.Configuration;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Surface for the orchestrator + supervisor flag toggles. Read open
/// (so the UI can render the panel without an X-Client-Id), write
/// gated by the registration boundary in
/// <see cref="OrchestratorApi.Services.Clients.ClientIdentityMiddleware"/>.
/// All flags require a backend restart to take effect; the response
/// surfaces that explicitly so the frontend can render the
/// "saved and active" state.
/// </summary>
public static class AdminConfigEndpoints
{
    public static void MapAdminConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/config");

        group.MapGet("/orchestrator", (OrchestratorConfigService svc) =>
        {
            return Results.Ok(svc.GetSnapshot());
        });

        group.MapPut("/orchestrator", (
            UpdateOrchestratorConfigRequest req,
            OrchestratorConfigService svc) =>
        {
            if (req?.Values == null || req.Values.Count == 0)
            {
                return Results.BadRequest(new { error = "values is required" });
            }
            try
            {
                var snapshot = svc.ApplyOverrides(req.Values);
                return Results.Ok(snapshot);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGroup("/api/maintenance").MapPost("/backfill-keys", (JobMutationService mutations) =>
        {
            var count = mutations.BackfillTaskKeys();
            return Results.Ok(new { stamped = count });
        });
    }
}

public sealed class UpdateOrchestratorConfigRequest
{
    public Dictionary<string, JsonElement>? Values { get; set; }
}
