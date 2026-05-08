using OrchestratorApi.Services.AdHoc;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read-only HTTP surface for the ad-hoc Haiku usage log. The
/// status-bar usage modal renders <c>GET /api/adhoc-usage</c> in a
/// dedicated section; the path returned in the response body lets the
/// UI also render an "open log" affordance.
///
/// <list type="bullet">
///   <item><c>GET /api/adhoc-usage</c> - rolled-up totals + per-source
///         + per-day + per-model. Optional <c>?since=ISO8601</c> filters
///         the rollup to records newer than the supplied UTC timestamp.</item>
///   <item><c>GET /api/adhoc-usage/log-path</c> - just the resolved log
///         path, for "Reveal in Explorer" / "open in default app" UI.</item>
/// </list>
/// </summary>
public static class AdHocUsageEndpoints
{
    public static void MapAdHocUsageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/adhoc-usage");

        group.MapGet("/",
            (string? since, AdHocUsageService svc) =>
            {
                DateTime? cutoff = null;
                if (!string.IsNullOrWhiteSpace(since)
                    && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    cutoff = parsed;
                }
                return Results.Ok(svc.Aggregate(cutoff));
            });

        group.MapGet("/log-path",
            (AdHocUsageRecorder recorder) =>
            {
                var (size, modified) = recorder.Stat();
                return Results.Ok(new
                {
                    path = recorder.LogPath,
                    sizeBytes = size,
                    modifiedAt = modified?.ToString("o")
                });
            });
    }
}
