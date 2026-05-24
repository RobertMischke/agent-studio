using OrchestratorApi.Models;
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
    public static void MapRegistryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspaces", (WorkspaceRegistry workspaces, ProjectRegistry projects) =>
        {
            var projectsByWs = projects.List()
                .Where(p => !p.Archived)
                .GroupBy(p => p.WorkspaceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var payload = workspaces.List().Select(w => new WorkspaceListItem
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

            return Results.Ok(payload);
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
    }
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
