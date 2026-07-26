using AgentStudio.Docs;

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
            CancellationToken ct) => Build("global", false, null, digests, ct));
        group.MapGet("/project:{projectId}", (
            string projectId,
            HttpRequest request,
            WorkbenchCatalogueService workbenches,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => BuildFromQuery(
                projectId, false, request, workbenches, digests, ct));
        group.MapGet("/task:{projectId}/{taskKey}", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", false, null, digests, ct));

        group.MapPost("/global/refresh", (
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build("global", true, null, digests, ct));
        group.MapPost("/project:{projectId}/refresh", (
            string projectId,
            HttpRequest request,
            WorkbenchCatalogueService workbenches,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => BuildFromQuery(
                projectId, true, request, workbenches, digests, ct));
        group.MapPost("/task:{projectId}/{taskKey}/refresh", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", true, null, digests, ct));

        // Body-based inspection is convenient for a validated bridge
        // selection and remains read-only. It resolves the same attachment as
        // a chat turn but never queues a model call or changes repository data.
        group.MapPost("/project:{projectId}/workbench", (
            string projectId,
            WorkbenchAttachmentRequest? request,
            WorkbenchCatalogueService workbenches,
            OrchestratorContextDigestService digests,
            CancellationToken ct) =>
        {
            if (request == null)
                return Task.FromResult<IResult>(
                    Results.BadRequest(new { error = "Workbench attachment request is required." }));
            try
            {
                return Build(
                    $"project:{projectId}",
                    false,
                    workbenches.ResolveAttachment(projectId, request),
                    digests,
                    ct);
            }
            catch (WorkbenchAttachmentException ex)
            {
                return Task.FromResult(AttachmentError(ex));
            }
        });
    }

    private static Task<IResult> BuildFromQuery(
        string projectId,
        bool forceQuotaRefresh,
        HttpRequest request,
        WorkbenchCatalogueService workbenches,
        OrchestratorContextDigestService digests,
        CancellationToken ct)
    {
        try
        {
            return Build(
                $"project:{projectId}",
                forceQuotaRefresh,
                ResolveQueryAttachment(projectId, request, workbenches),
                digests,
                ct);
        }
        catch (WorkbenchAttachmentException ex)
        {
            return Task.FromResult(AttachmentError(ex));
        }
    }

    private static async Task<IResult> Build(
        string rawContextKey,
        bool forceQuotaRefresh,
        WorkbenchContextAttachment? workbench,
        OrchestratorContextDigestService digests,
        CancellationToken ct)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var context))
            return Results.BadRequest(new { error = "Invalid orchestrator context key." });

        try
        {
            var response = workbench == null
                ? await digests.BuildAsync(context, forceQuotaRefresh, ct).ConfigureAwait(false)
                : await digests.BuildAsync(context, workbench, forceQuotaRefresh, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (WorkbenchAttachmentException ex)
        {
            return AttachmentError(ex);
        }
    }

    private static WorkbenchContextAttachment? ResolveQueryAttachment(
        string projectId,
        HttpRequest request,
        WorkbenchCatalogueService workbenches)
    {
        var id = request.Query["workbenchId"].ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;
        var selectionKey = request.Query["selectionKey"].ToString();
        var selectionValue = request.Query["selectionValue"].ToString();
        WorkbenchPresentationSelection? selection = null;
        if (!string.IsNullOrWhiteSpace(selectionKey) || !string.IsNullOrWhiteSpace(selectionValue))
        {
            selection = new WorkbenchPresentationSelection(
                selectionKey,
                selectionValue,
                EmptyToNull(request.Query["selectionLabel"].ToString()));
        }
        return workbenches.ResolveAttachment(
            projectId,
            new WorkbenchAttachmentRequest(
                id,
                EmptyToNull(request.Query["revision"].ToString()),
                EmptyToNull(request.Query["contentFingerprint"].ToString()),
                selection));
    }

    internal static IResult AttachmentError(WorkbenchAttachmentException ex) => ex.Code switch
    {
        "not-found" => Results.NotFound(new { error = ex.Message, code = ex.Code }),
        "stale" => Results.Conflict(new { error = ex.Message, code = ex.Code }),
        _ => Results.BadRequest(new { error = ex.Message, code = ex.Code }),
    };

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
