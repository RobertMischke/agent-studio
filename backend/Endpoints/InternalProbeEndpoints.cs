using System.Text.Json;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// ADR-0031 phase-6 db-touch sentinel: a POST endpoint that round-trips
/// the request body. The Update Service's verifier hits it after a
/// restart to prove the .NET request pipeline is alive end-to-end (not
/// just /healthz which is a static literal).
///
/// Gated by Environment:IsDev. The endpoint is registered unconditionally,
/// but it returns 404 when the gate is off so production callers cannot
/// see it. We deliberately do NOT use the routing-time gate here so the
/// behaviour is observable in logs and overridable for tests.
/// </summary>
public static class InternalProbeEndpoints
{
    public static void MapInternalProbeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/_internal/probe", async (HttpContext ctx, IConfiguration config) =>
        {
            var isDev = config.GetValue<bool>("Environment:IsDev");
            if (!isDev) return Results.NotFound();

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
