using OrchestratorApi.Models;
using OrchestratorApi.Services.Configuration;
using OrchestratorApi.Services.Registry;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// F45a — read-only surface for the workspace + project registries.
/// Lists workspaces with their embedded projects, returns project
/// details by PROJ-NNN id, and lists projects flat.
///
/// <para>Write endpoints (POST / PUT / DELETE) ship in F45b alongside
/// folder-skeleton creation and rename semantics. The new endpoints are
/// additive: existing <c>/api/projects/{projectName}/...</c> routes
/// continue to operate against display names, and the new
/// <c>/api/projects/{projId}</c> route is constrained to the canonical
/// <c>PROJ-NNN</c> shape so it cannot accidentally swallow a legacy name
/// like <c>settings</c> or a watched-project display name.</para>
/// </summary>
public static class RegistryEndpoints
{
    /// <summary>
    /// F66 — pure projection used by <c>GET /api/workspaces</c>. Extracted so
    /// the LEFT-JOIN invariant ("every workspace appears, even ones with no
    /// projects") can be locked in by a unit test without hosting Kestrel.
    /// Iteration anchors on <c>workspaces.List()</c> and the per-workspace
    /// projects list defaults to an empty list when no projects are mapped.
    /// </summary>
    public static List<WorkspaceListItem> BuildWorkspaceListing(
        WorkspaceRegistry workspaces,
        ProjectRegistry projects,
        bool includeArchived)
    {
        var projectsByWs = projects.List()
            .Where(p => includeArchived || !p.Archived)
            .GroupBy(p => p.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return workspaces.List().Select(w => new WorkspaceListItem
        {
            Id = w.Id,
            DisplayName = w.DisplayName,
            SortOrder = w.SortOrder,
            IsDefault = w.IsDefault,
            Color = w.Color,
            CreatedAt = w.CreatedAt,
            Projects = projectsByWs.TryGetValue(w.Id, out var list)
                ? list.Select(ProjectSummary.From).ToList()
                : [],
        }).ToList();
    }

    public static void MapRegistryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspaces", (WorkspaceRegistry workspaces, ProjectRegistry projects, bool? includeArchived) =>
        {
            return Results.Ok(BuildWorkspaceListing(workspaces, projects, includeArchived == true));
        });

        app.MapGet("/api/projects", (ProjectRegistry projects, bool? includeArchived) =>
        {
            var all = projects.List();
            if (includeArchived != true) all = [.. all.Where(p => !p.Archived)];
            return Results.Ok(all.Select(ProjectSummary.From).ToList());
        });

        // The {projId} route is constrained to PROJ-NNN so it cannot
        // collide with the legacy /api/projects/{projectName}/...
        // family (settings, Runbook, etc.) which uses display names.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}", (string projId, ProjectRegistry projects) =>
        {
            var record = projects.FindById(projId);
            return record == null
                ? Results.NotFound(new { error = $"Unknown projectId '{projId}'" })
                : Results.Ok(record);
        });

        // ----- F45b workspace mutations (ADR-0042) -----

        app.MapPost("/api/workspaces", (RegistryCreateWorkspaceRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "displayName is required" });
            try
            {
                var created = workspaces.Create(body.DisplayName, body.Color);
                return Results.Created($"/api/workspaces/{created.Id}", created);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/workspaces/{id}", (string id, UpdateWorkspaceRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                WorkspaceRecord? result = null;
                if (body.DisplayName != null) result = workspaces.Rename(id, body.DisplayName);
                if (body.Color != null || body.ClearColor == true)
                    result = workspaces.SetColor(id, body.ClearColor == true ? null : body.Color);
                if (result == null) result = workspaces.Find(id);
                return result == null
                    ? Results.NotFound(new { error = $"Unknown workspaceId '{id}'" })
                    : Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/workspaces/{id}/reorder", (string id, WorkspaceReorderRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null || (body.Direction != -1 && body.Direction != 1))
                return Results.BadRequest(new { error = "direction must be -1 or +1" });
            try
            {
                var list = workspaces.Reorder(id, body.Direction);
                return Results.Ok(list);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapDelete("/api/workspaces/{id}", (string id, WorkspaceRegistry workspaces, ProjectRegistry projects) =>
        {
            try
            {
                var result = workspaces.Delete(id, projects);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // ----- F45b project mutations (ADR-0042) -----

        app.MapPut(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}", (string projId, UpdateProjectRequest body, ProjectRegistry projects, WorkspaceRegistry workspaces) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                ProjectRecord? result = null;
                if (body.DisplayName != null) result = projects.Rename(projId, body.DisplayName);
                if (body.ShortCode != null) result = projects.SetShortCode(projId, body.ShortCode);
                if (body.Color != null || body.ClearColor == true)
                    result = projects.SetColor(projId, body.ClearColor == true ? null : body.Color);
                if (body.WorkspaceId != null) result = projects.SetWorkspace(projId, body.WorkspaceId, workspaces);
                if (body.Archived.HasValue) result = projects.SetArchived(projId, body.Archived.Value);
                if (result == null) result = projects.FindById(projId);
                return result == null
                    ? Results.NotFound(new { error = $"Unknown projectId '{projId}'" })
                    : Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // F46 — destructive project delete. Removes the on-disk project
        // storage (every lane + task), drops the matching WatchPaths entry so
        // no ghost picker row survives, then removes the registry record.
        // Storage is deleted first so a failure aborts before any metadata is
        // touched — never leaving an orphan folder behind a dangling pointer.
        app.MapDelete(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}",
            (string projId, ProjectRegistry projects, WorkspaceManagementService workspaceManagement, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("ProjectDelete");
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });

            var storageResult = workspaceManagement.DeleteProjectStorage(record.StorageLocation);
            if (storageResult.Outcome == WorkspaceManagementOutcome.BadRequest)
            {
                log.LogError(
                    "project-delete-storage-failed id={Id} storage={Storage} error={Error}",
                    record.Id, record.StorageLocation, storageResult.Error);
                return Results.Problem(
                    storageResult.Error ?? "Failed to delete project storage.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            try { projects.Delete(projId); }
            catch (KeyNotFoundException) { /* already removed; idempotent success */ }

            log.LogInformation(
                "project-deleted id={Id} displayName={DisplayName} storage={Storage}",
                record.Id, record.DisplayName, record.StorageLocation);

            return Results.Ok(new
            {
                deletedId = record.Id,
                displayName = record.DisplayName,
                storageLocation = record.StorageLocation,
            });
        });
    }
}

/// <summary>F45b — POST /api/workspaces payload.</summary>
public sealed record RegistryCreateWorkspaceRequest
{
    public string DisplayName { get; init; } = "";
    public string? Color { get; init; }
}

/// <summary>
/// F45b — PUT /api/workspaces/{id} payload. Each field is optional; only
/// non-null fields are applied. Set <see cref="ClearColor"/> = true to clear
/// the color (cannot be expressed by sending null because null also means
/// "leave unchanged").
/// </summary>
public sealed record UpdateWorkspaceRequest
{
    public string? DisplayName { get; init; }
    public string? Color { get; init; }
    public bool? ClearColor { get; init; }
}

/// <summary>F45b — POST /api/workspaces/{id}/reorder payload.</summary>
public sealed record WorkspaceReorderRequest
{
    /// <summary>-1 = move up one slot; +1 = move down one slot.</summary>
    public int Direction { get; init; }
}

/// <summary>
/// F45b — PUT /api/projects/{PROJ-NNN} payload. Same optional-field
/// semantics as <see cref="UpdateWorkspaceRequest"/>.
/// </summary>
public sealed record UpdateProjectRequest
{
    public string? DisplayName { get; init; }
    public string? ShortCode { get; init; }
    public string? Color { get; init; }
    public bool? ClearColor { get; init; }
    public string? WorkspaceId { get; init; }
    public bool? Archived { get; init; }
}

/// <summary>
/// API DTO returned by <c>GET /api/workspaces</c>. Mirrors
/// <see cref="WorkspaceRecord"/> plus an inline list of projects so the
/// client can render the sidebar with a single round-trip.
/// </summary>
public sealed record WorkspaceListItem
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int SortOrder { get; init; }
    public bool IsDefault { get; init; }
    public string? Color { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ProjectSummary> Projects { get; init; } = [];
}

/// <summary>
/// Flat-list shape for <c>GET /api/projects</c> and the embedded list
/// inside <c>WorkspaceListItem.Projects</c>. Omits the
/// <see cref="ProjectRecord.NextTaskKeySeq"/> counter to avoid surfacing
/// internal state on a list call - <c>GET /api/projects/{id}</c> returns
/// the full record for callers that need it.
/// </summary>
public sealed record ProjectSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ShortCode { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? Color { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public int SortOrder { get; init; }
    public string StorageLocation { get; init; } = "";
    public bool Archived { get; init; }
    public DateTime CreatedAt { get; init; }

    public static ProjectSummary From(ProjectRecord p) => new()
    {
        Id = p.Id,
        DisplayName = p.DisplayName,
        ShortCode = p.ShortCode,
        WorkspaceId = p.WorkspaceId,
        Color = p.Color,
        CliDefault = p.CliDefault,
        ModelDefault = p.ModelDefault,
        SortOrder = p.SortOrder,
        StorageLocation = p.StorageLocation,
        Archived = p.Archived,
        CreatedAt = p.CreatedAt,
    };
}
