namespace AgentStudio.TunnelSupervision;

/// <summary>
/// Read-only visibility for the Windows control-plane host's tunnel keeper
/// and watchdog (AGT-2664). Folds a loose pair of PowerShell scripts into the
/// product's setup and admin surface: see
/// docs/operations/setup/windows-control-plane-host.md.
/// </summary>
public static class TunnelSupervisionEndpoints
{
    public static void MapTunnelSupervisionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/tunnel-supervision", (TunnelSupervisionStatusReader reader) =>
        {
            var snapshot = reader.Read();
            return Results.Ok(new TunnelSupervisionResponse(
                TunnelSupervisionPolicy.Classify(snapshot, DateTime.UtcNow),
                snapshot));
        });
    }
}

public sealed record TunnelSupervisionResponse(string Overall, TunnelSupervisionSnapshot? Snapshot);
