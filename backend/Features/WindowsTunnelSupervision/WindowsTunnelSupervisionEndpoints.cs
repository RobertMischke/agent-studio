namespace AgentStudio.WindowsTunnelSupervision;

/// <summary>
/// Read-only status for the Windows control-plane host setup: whether the
/// tunnel keeper and watchdog Scheduled Tasks are registered, running, and
/// when the watchdog last healed the tunnel. Registration itself happens out
/// of band, from the self-elevating
/// <c>deploy/windows/agent-runner-tunnel/install-tunnel-supervision.ps1</c>;
/// this endpoint only reports what it finds.
/// </summary>
public static class WindowsTunnelSupervisionEndpoints
{
    public static void MapWindowsTunnelSupervisionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/windows-tunnel-supervision/status", async (
            HttpContext context,
            IWindowsTunnelSupervisionService service,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.GetStatusAsync(cancellationToken));
        });
    }
}
