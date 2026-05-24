using OrchestratorApi.Services.Projection;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Routes for the F22 conversation projection (server-rendered chat events).
///
/// Layout:
/// <list type="bullet">
/// <item><c>GET /api/jobs/{jobId}/conversation</c> - full snapshot, optionally
///   restricted to events after <c>?sinceMs=</c>.</item>
/// <item><c>POST /api/jobs/{jobId}/conversation/invalidate</c> - test /
///   debug hook that clears the cached projection for the given job.</item>
/// </list>
///
/// The endpoints serve from the in-memory <see cref="ConversationCache"/>;
/// callbacks from <see cref="SourceWatcher"/> push deltas on the
/// <c>conv-{jobId}</c> SignalR group so clients normally do not need to
/// poll. The feature flag <c>ConversationProjection:BackendEnabled</c>
/// gates only the live-broadcast path; the read endpoint stays live so the
/// new pipeline can be exercised in isolation.
/// </summary>
public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId}/conversation",
            async (string jobId, string? watchPath, long? sinceMs, ConversationProjector projector, CancellationToken ct) =>
            {
                var opts = new ProjectionOptions
                {
                    SinceUtc = sinceMs.HasValue
                        ? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs.Value).UtcDateTime
                        : null
                };
                var events = await projector.ProjectAsync(jobId, watchPath, opts, ct).ConfigureAwait(false);
                return Results.Ok(events);
            });

        app.MapPost("/api/jobs/{jobId}/conversation/invalidate",
            (string jobId, ConversationProjector projector) =>
            {
                projector.Invalidate(jobId);
                return Results.NoContent();
            });
    }
}
