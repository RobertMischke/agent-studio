namespace AgentStudio.Orchestrator;

/// <summary>
/// Read-only ORCH-1 context surface. GET builds from live cheap sources and
/// cached quota. POST /refresh expresses explicit operator intent and waits
/// for the existing quota probes before rebuilding the same response shape.
/// </summary>
public static class OrchestratorContextEndpoints
{
    public static void MapOrchestratorContextEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orchestrator/context");

        group.MapGet("/global", (
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build("global", false, digests, ct));
        group.MapGet("/project:{projectId}", (
            string projectId,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"project:{projectId}", false, digests, ct));
        group.MapGet("/task:{projectId}/{taskKey}", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", false, digests, ct));
        group.MapGet("/dossier:{projectId}/{dossierKey}", (
            string projectId,
            string dossierKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"dossier:{projectId}/{dossierKey}", false, digests, ct));

        group.MapPost("/global/refresh", (
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build("global", true, digests, ct));
        group.MapPost("/project:{projectId}/refresh", (
            string projectId,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"project:{projectId}", true, digests, ct));
        group.MapPost("/task:{projectId}/{taskKey}/refresh", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", true, digests, ct));
        group.MapPost("/dossier:{projectId}/{dossierKey}/refresh", (
            string projectId,
            string dossierKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"dossier:{projectId}/{dossierKey}", true, digests, ct));
    }

    private static async Task<IResult> Build(
        string rawContextKey,
        bool forceQuotaRefresh,
        OrchestratorContextDigestService digests,
        CancellationToken ct)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var context))
            return Results.BadRequest(new { error = "Invalid orchestrator context key." });

        try
        {
            var response = await digests.BuildAsync(context, forceQuotaRefresh, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }
}
