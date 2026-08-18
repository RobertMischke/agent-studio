namespace AgentStudio.HostHealth;

/// <summary>
/// Read surface for local CLI install health plus an on-demand repair. The
/// status bar polls the first route for the "CLI repaired at &lt;time&gt;" note;
/// the second exists so a diagnosis that is not licensed for automatic repair
/// (or one that is still inside the rate-limit window) is not a dead end for
/// the operator.
/// </summary>
public static class HostHealthEndpoints
{
    public static void MapHostHealthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/host-health");

        group.MapGet("/cli", (LocalCliHealthService health) => Results.Ok(health.Inspect()));

        group.MapPost("/cli/{cliType}/repair", async (
            string cliType,
            LocalCliHealthService health,
            CancellationToken ct) =>
        {
            if (LocalCliPackage.Find(cliType) is null)
            {
                return Results.Json(
                    new { error = "unknown-cli-type", message = $"'{cliType}' is not an npm-installed CLI this host can repair." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var entry = await health.EnsureHealthyAsync(cliType, operatorRequested: true, ct);
            return Results.Ok(entry);
        });
    }
}
