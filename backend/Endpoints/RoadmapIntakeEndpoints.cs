using OrchestratorApi.Models;
using OrchestratorApi.Services;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Two-step roadmap intake. <c>POST /api/roadmap/intake</c> runs the
/// splitter and returns candidates with no side effects so the user can
/// review and edit. <c>POST /api/roadmap/intake/confirm</c> materialises
/// the chosen subset as job folders in <c>1-preparation</c>. Intake
/// never lands jobs in <c>2-ready</c> - by design, every roadmap dump
/// gets one human pass on the board before it can be queued.
/// </summary>
public static class RoadmapIntakeEndpoints
{
    public static void MapRoadmapIntakeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/roadmap/intake", async (RoadmapIntakeRequest req, RoadmapIntakeService svc, CancellationToken ct) =>
        {
            if (req == null) return Results.BadRequest(new { error = "Body is required" });
            // WatchPath is required so the preview UI can show the user which
            // project the eventual jobs would land in. The split itself is
            // project-agnostic, but the confirm step needs it.
            if (string.IsNullOrWhiteSpace(req.WatchPath))
                return Results.BadRequest(new { error = "watchPath is required" });

            try
            {
                var resp = await svc.SplitAsync(req.Text ?? "", ct);
                return Results.Ok(resp);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        app.MapPost("/api/roadmap/intake/confirm", (RoadmapIntakeConfirmRequest req, RoadmapIntakeService svc) =>
        {
            if (req == null) return Results.BadRequest(new { error = "Body is required" });
            if (string.IsNullOrWhiteSpace(req.WatchPath))
                return Results.BadRequest(new { error = "watchPath is required" });
            if (req.Candidates == null || req.Candidates.Count == 0)
                return Results.BadRequest(new { error = "candidates is required" });

            var resp = svc.Confirm(req);
            return Results.Ok(resp);
        });
    }
}
