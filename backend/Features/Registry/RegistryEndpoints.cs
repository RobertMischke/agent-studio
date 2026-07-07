

namespace AgentStudio.Registry;

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

        app.MapPost("/api/projects", (RegistryCreateProjectRequest body, ProjectRegistry projects, WorkspaceRegistry workspaces, WorkspaceManagementService workspaceManagement, ILoggerFactory loggerFactory) =>
        {
            if (body == null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "displayName is required" });
            if (string.IsNullOrWhiteSpace(body.WorkspaceId))
                return Results.BadRequest(new { error = "workspaceId is required" });
            if (workspaces.Find(body.WorkspaceId) == null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{body.WorkspaceId}'" });

            var displayName = body.DisplayName.Trim();
            var allProjects = projects.List();
            var existingCodes = allProjects.Select(p => p.ShortCode);
            var shortCode = string.IsNullOrWhiteSpace(body.ShortCode)
                ? ShortCodeGenerator.Derive(displayName, existingCodes)
                : body.ShortCode.Trim().ToUpperInvariant();
            if (!ShortCodeGenerator.ValidateFormat(shortCode))
                return Results.BadRequest(new { error = "shortCode must be 2-6 chars, start with A-Z, and use A-Z or 0-9" });
            if (allProjects.Any(p => string.Equals(p.ShortCode, shortCode, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = $"shortCode '{shortCode}' is already used." });

            var id = projects.AllocateNextId();
            var storage = workspaceManagement.CreateProjectStorage(displayName, id);
            if (storage.Outcome == WorkspaceManagementOutcome.BadRequest)
                return Results.BadRequest(new { error = storage.Error });
            if (storage.Outcome == WorkspaceManagementOutcome.Conflict)
                return Results.Conflict(new { error = storage.Error });

            var record = new ProjectRecord
            {
                Id = id,
                DisplayName = displayName,
                ShortCode = shortCode,
                WorkspaceId = body.WorkspaceId.Trim(),
                Color = string.IsNullOrWhiteSpace(body.Color) ? null : body.Color.Trim(),
                CliDefault = string.IsNullOrWhiteSpace(body.CliDefault) ? null : body.CliDefault.Trim(),
                ModelDefault = string.IsNullOrWhiteSpace(body.ModelDefault) ? null : body.ModelDefault.Trim(),
                SortOrder = allProjects.Count,
                NextTaskKeySeq = 1,
                StorageLocation = storage.Entry?.Path ?? "",
                Archived = false,
                CreatedAt = DateTime.UtcNow,
            };

            try
            {
                var created = projects.Append(record);
                if (!string.IsNullOrWhiteSpace(body.RootPath))
                    created = projects.SetRootPath(created.Id, body.RootPath);
                loggerFactory.CreateLogger("ProjectCreate").LogInformation(
                    "project-created id={Id} workspaceId={WorkspaceId} storage={Storage}",
                    created.Id, created.WorkspaceId, created.StorageLocation);
                return Results.Created($"/api/projects/{created.Id}", ProjectSummary.From(created));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

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
                if (body.RepositoryPath != null || body.ClearRepositoryPath == true)
                    result = projects.SetRepositoryPath(projId, body.ClearRepositoryPath == true ? null : body.RepositoryPath);
                if (body.RootPath != null || body.ClearRootPath == true)
                    result = projects.SetRootPath(projId, body.ClearRootPath == true ? null : body.RootPath);
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
            catch (KeyNotFoundException __ex) { SilentCatch.Note(__ex, "RegistryEndpoints: already removed; idempotent success"); /* already removed; idempotent success */ }

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

        // ----- Project URLs (per-project watchable dev-server / preview URLs) -----

        // Detection: scan the project's repository (package.json, angular.json,
        // README.md) and return suggestions the UI offers as one-click chips.
        // Never auto-applied; the user picks.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/url-suggestions",
            (string projId, ProjectRegistry projects, ProjectUrlDetectionService detection) =>
        {
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var suggestions = detection.Detect(record);
            return Results.Ok(suggestions);
        });

        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls",
            (string projId, CreateProjectUrlRequest body, ProjectRegistry projects) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var updated = projects.AddUrl(projId, body.Label, body.Url, body.StartRule);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}",
            (string projId, string urlId, UpdateProjectUrlRequest body, ProjectRegistry projects) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var updated = projects.UpdateUrl(projId, urlId, body.Label, body.Url, body.StartRule);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}",
            (string projId, string urlId, ProjectRegistry projects) =>
        {
            try
            {
                var updated = projects.RemoveUrl(projId, urlId);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/reorder",
            (string projId, ReorderProjectUrlsRequest body, ProjectRegistry projects) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var updated = projects.ReorderUrls(projId, body.OrderedUrlIds);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Build & start / restart a URL's dev server (spawns StartRule.Command
        // in Cwd, default RepositoryPath). Surfacing stdout/stderr is future
        // scope; this only gets the process running.
        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/start",
            (string projId, string urlId, ProjectRegistry projects, ProjectUrlProcessService procs) =>
        {
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var url = record.Urls.FirstOrDefault(u => string.Equals(u.Id, urlId, StringComparison.Ordinal));
            if (url == null)
                return Results.NotFound(new { error = $"Unknown url id '{urlId}'" });
            if (url.StartRule == null || string.IsNullOrWhiteSpace(url.StartRule.Command))
                return Results.BadRequest(new { error = "This URL has no start rule to run." });
            try
            {
                procs.Start(record, url);
                return Results.Ok(new { started = true, urlId = url.Id });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

/// <summary>POST /api/projects/{id}/urls payload.</summary>
public sealed record CreateProjectUrlRequest
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public ProjectUrlStartRule? StartRule { get; init; }
}

/// <summary>PUT /api/projects/{id}/urls/{urlId} payload.</summary>
public sealed record UpdateProjectUrlRequest
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public ProjectUrlStartRule? StartRule { get; init; }
}

/// <summary>POST /api/projects/{id}/urls/reorder payload.</summary>
public sealed record ReorderProjectUrlsRequest
{
    public List<string> OrderedUrlIds { get; init; } = [];
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

/// <summary>F46 — POST /api/projects payload.</summary>
public sealed record RegistryCreateProjectRequest
{
    public string WorkspaceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ShortCode { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public string? Color { get; init; }
    /// <summary>
    /// Optional CLI working directory, set at onboarding time so auto-pickup
    /// has a runner from the first boot instead of silently having none
    /// until someone notices the mode toggle failing (see
    /// <see cref="ProjectRecord.RootPath"/>).
    /// </summary>
    public string? RootPath { get; init; }
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
    /// <summary>Absolute repo checkout path; see <see cref="ProjectRecord.RepositoryPath"/>.</summary>
    public string? RepositoryPath { get; init; }
    public bool? ClearRepositoryPath { get; init; }
    /// <summary>Absolute CLI working directory; see <see cref="ProjectRecord.RootPath"/>.</summary>
    public string? RootPath { get; init; }
    public bool? ClearRootPath { get; init; }
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
    /// <summary>Configured watchable URLs, ordered; empty for most projects.</summary>
    public IReadOnlyList<ProjectUrlRecord> Urls { get; init; } = [];
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
        Urls = [.. p.Urls.OrderBy(u => u.SortOrder)],
        Archived = p.Archived,
        CreatedAt = p.CreatedAt,
    };
}
