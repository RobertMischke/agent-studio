using System.Text.Json;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// ADR-0031 phase-6 db-touch sentinel: a POST endpoint that round-trips
/// the request body. The Update Service's verifier hits it after a
/// restart to prove the .NET request pipeline is alive end-to-end (not
/// just /healthz which is a static literal).
///
/// Gated by either Environment:IsDev or DevTools:UpdateStableEnabled. The
/// stable instance is not branded as dev, but its local operator config
/// enables the update tool; phase-6 verification must still be able to
/// prove the restarted backend pipeline can round-trip JSON. The endpoint
/// is registered unconditionally, but returns 404 when both gates are off
/// so production callers cannot see it.
/// </summary>
public static class InternalProbeEndpoints
{
    public static void MapInternalProbeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/_internal/probe", async (HttpContext ctx, IConfiguration config) =>
        {
            var enabled = config.GetValue<bool>("Environment:IsDev")
                || config.GetValue<bool>("DevTools:UpdateStableEnabled");
            if (!enabled) return Results.NotFound();

            // Read the body, deserialise as JsonElement so we can echo any shape,
            // and return it back wrapped with a server-side timestamp.
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "invalid json", detail = ex.Message }, statusCode: 400);
            }

            return Results.Json(new
            {
                ok = true,
                receivedAt = DateTime.UtcNow,
                echo = body
            });
        });
    }
}
