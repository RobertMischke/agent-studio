using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Registry;

/// <summary>
/// F45a — in-memory cache plus persistence for the project catalog under
/// <c>&lt;TaskRepository&gt;/.metadata/projects.json</c>. Owns:
///
/// <list type="bullet">
/// <item>Allocating new <c>PROJ-NNN</c> ids from the global
/// <c>nextProjectIdSeq</c> counter (monotonic, never re-used).</item>
/// <item>Resolving an existing project by id, by storage path, or by
/// (display-name or id) string - the lookup that backs the existing
/// <c>?project=...</c> query semantics.</item>
/// <item>Issuing the next per-project task key (the F33 counter, now
/// reparented onto the project record itself instead of a sidecar
/// <c>.task-counter.json</c> per watch-path folder).</item>
/// </list>
///
/// <para>F45a ships the read surface plus boot-time auto-discovery
/// (<see cref="EnsureProjectForStorage"/>) that populates the registry
/// from the configured <c>WatchPaths</c>. Project creation via REST,
/// rename, archive, and workspace reassignment land in F45b.</para>
/// </summary>
public sealed class ProjectRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Regex ProjIdFormat = new(@"^PROJ-\d{3,}$", RegexOptions.Compiled);

    private readonly string? _taskRepositoryRoot;
    private readonly ILogger<ProjectRegistry> _logger;
    private readonly object _gate = new();
    private ProjectsFile _state = new();
    private bool _loaded;

    public ProjectRegistry(IConfiguration config, ILogger<ProjectRegistry> logger)
    {
        _logger = logger;
        var raw = config["TaskRepository"];
        _taskRepositoryRoot = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    public bool IsPersistent => _taskRepositoryRoot != null;

    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (_taskRepositoryRoot == null) return;

            var path = RegistryPaths.ProjectsFilePath(_taskRepositoryRoot);
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<ProjectsFile>(json, JsonOpts);
                if (parsed != null)
                {
                    _state = parsed;
                    _logger.LogInformation(
                        "project-registry-loaded path={Path} count={Count} nextSeq={Next}",
                        path, _state.Projects.Count, _state.NextProjectIdSeq);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "project-registry-load-failed path={Path} - starting with empty list",
                    path);
            }
        }
    }

    /// <summary>
    /// Snapshot of the current projects, sorted by sort-order then display
    /// name. Archived entries are included; callers filter as needed.
    /// </summary>
    public List<ProjectRecord> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public ProjectRecord? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Returns the project whose <see cref="ProjectRecord.StorageLocation"/>
    /// matches the supplied absolute path (case-insensitive, slash-normalised).
    /// Backs the F45 jobKey shim that translates legacy
    /// <c>&lt;path&gt;::&lt;slug&gt;</c> keys into <c>PROJ-NNN::&lt;slug&gt;</c>.
    /// </summary>
    public ProjectRecord? FindByStorageLocation(string? storageLocation)
    {
        if (string.IsNullOrWhiteSpace(storageLocation)) return null;
        var normalised = NormalisePath(storageLocation);
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects.FirstOrDefault(p =>
                string.Equals(NormalisePath(p.StorageLocation), normalised, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Convenience lookup that accepts either a project id (<c>PROJ-001</c>)
    /// or a display name. Display-name match is case-insensitive. Returns
    /// null when nothing matches.
    /// </summary>
    public ProjectRecord? FindByIdOrDisplayName(string? idOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(idOrDisplayName)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            var byId = _state.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;

            return _state.Projects.FirstOrDefault(p =>
                string.Equals(p.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Boot-time hook used by the discovery pass. If a project with the
    /// given <paramref name="storageLocation"/> already exists, returns it.
    /// Otherwise allocates the next PROJ id, derives a short code from
    /// <paramref name="initialDisplayName"/>, appends a new record, and
    /// persists.
    /// </summary>
    public ProjectRecord EnsureProjectForStorage(
        string storageLocation,
        string initialDisplayName,
        string workspaceId,
        TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(storageLocation))
            throw new ArgumentException("storageLocation is required", nameof(storageLocation));
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required", nameof(workspaceId));

        clock ??= TimeProvider.System;
        EnsureLoaded();
        lock (_gate)
        {
            var normalised = NormalisePath(storageLocation);
            var existing = _state.Projects.FirstOrDefault(p =>
                string.Equals(NormalisePath(p.StorageLocation), normalised, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var newId = AllocateNextIdLocked();
            var existingCodes = _state.Projects.Select(p => p.ShortCode);
            var shortCode = ShortCodeGenerator.Derive(initialDisplayName, existingCodes);
            var record = new ProjectRecord
            {
                Id = newId,
                DisplayName = string.IsNullOrWhiteSpace(initialDisplayName) ? newId : initialDisplayName,
                ShortCode = shortCode,
                WorkspaceId = workspaceId,
                SortOrder = _state.Projects.Count,
                NextTaskKeySeq = 1,
                StorageLocation = storageLocation,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            _state = _state with { Projects = [.. _state.Projects, record] };
            PersistLocked();
            _logger.LogInformation(
                "project-registry-auto-discovered id={Id} displayName={DisplayName} shortCode={ShortCode} storage={Storage}",
                record.Id, record.DisplayName, record.ShortCode, record.StorageLocation);
            return record;
        }
    }

    /// <summary>
    /// Atomically reserves the next task key for <paramref name="projectId"/>
    /// and persists. The counter is monotonic and per-project; nothing
    /// rolls it back. Throws <see cref="KeyNotFoundException"/> when the
    /// project id is unknown.
    /// </summary>
    public int IssueNextTaskKey(string projectId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new KeyNotFoundException($"Unknown projectId: {projectId}");

            var current = _state.Projects[idx];
            var issued = current.NextTaskKeySeq;
            var updated = current with { NextTaskKeySeq = issued + 1 };
            var next = _state.Projects.ToList();
            next[idx] = updated;
            _state = _state with { Projects = next };
            PersistLocked();
            return issued;
        }
    }

    /// <summary>
    /// Raises <see cref="ProjectRecord.NextTaskKeySeq"/> for the project
    /// to at least <paramref name="floor"/>. Used by the F45c migration
    /// after stamping existing tasks. No-op when already at or above.
    /// </summary>
    public void EnsureTaskKeyFloor(string projectId, int floor)
    {
        if (floor < 1) return;
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {projectId}");
            var current = _state.Projects[idx];
            if (current.NextTaskKeySeq >= floor) return;
            var next = _state.Projects.ToList();
            next[idx] = current with { NextTaskKeySeq = floor };
            _state = _state with { Projects = next };
            PersistLocked();
        }
    }

    /// <summary>F45b — rename a project's display name. Id is immutable.</summary>
    public ProjectRecord Rename(string id, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new ArgumentException("newDisplayName is required", nameof(newDisplayName));
        return MutateLocked(id, p => p with { DisplayName = newDisplayName.Trim() }, "renamed");
    }

    /// <summary>
    /// F45b — change the short code (2-6 chars, A-Z and 0-9). Validates and
    /// rejects duplicates against other active projects.
    /// </summary>
    public ProjectRecord SetShortCode(string id, string newShortCode)
    {
        if (string.IsNullOrWhiteSpace(newShortCode))
            throw new ArgumentException("newShortCode is required", nameof(newShortCode));
        var normalized = newShortCode.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[A-Z0-9]{2,6}$"))
            throw new ArgumentException(
                "shortCode must be 2-6 chars of A-Z and 0-9", nameof(newShortCode));
        EnsureLoaded();
        lock (_gate)
        {
            var collision = _state.Projects.FirstOrDefault(p =>
                !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.ShortCode, normalized, StringComparison.OrdinalIgnoreCase));
            if (collision != null)
                throw new InvalidOperationException(
                    $"shortCode '{normalized}' is already used by {collision.Id}.");
        }
        return MutateLocked(id, p => p with { ShortCode = normalized }, "short-code-set");
    }

    /// <summary>F45b — set the accent color (CSS hex). Pass null to clear.</summary>
    public ProjectRecord SetColor(string id, string? color)
        => MutateLocked(id, p => p with { Color = string.IsNullOrWhiteSpace(color) ? null : color }, "color-set");

    /// <summary>F45b — reassign a project to a different workspace.</summary>
    public ProjectRecord SetWorkspace(string id, string newWorkspaceId, WorkspaceRegistry workspaces)
    {
        if (string.IsNullOrWhiteSpace(newWorkspaceId))
            throw new ArgumentException("newWorkspaceId is required", nameof(newWorkspaceId));
        if (workspaces.Find(newWorkspaceId) == null)
            throw new KeyNotFoundException($"Unknown workspaceId: {newWorkspaceId}");
        return MutateLocked(id, p => p with { WorkspaceId = newWorkspaceId }, "workspace-set");
    }

    /// <summary>F45b — archive or un-archive a project.</summary>
    public ProjectRecord SetArchived(string id, bool archived)
        => MutateLocked(id, p => p with { Archived = archived }, archived ? "archived" : "unarchived");

    /// <summary>
    /// F46 — permanently remove a project record from the registry and
    /// return the removed record so the caller can act on its
    /// <see cref="ProjectRecord.StorageLocation"/>. Throws
    /// <see cref="KeyNotFoundException"/> when the id is unknown. This drops
    /// the metadata row only; deleting the on-disk project storage is the
    /// caller's responsibility (see <c>WorkspaceManagementService</c>) so the
    /// metadata authority stays decoupled from the filesystem authority.
    /// </summary>
    public ProjectRecord Delete(string id)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");
            var removed = _state.Projects[idx];
            _state = _state with
            {
                Projects = [.. _state.Projects.Where((_, i) => i != idx)],
            };
            PersistLocked();
            _logger.LogInformation(
                "project-registry-deleted id={Id} displayName={DisplayName} storage={Storage}",
                removed.Id, removed.DisplayName, removed.StorageLocation);
            return removed;
        }
    }

    private ProjectRecord MutateLocked(string id, Func<ProjectRecord, ProjectRecord> update, string op)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");
            var updated = update(_state.Projects[idx]);
            var next = _state.Projects.ToList();
            next[idx] = updated;
            _state = _state with { Projects = next };
            PersistLocked();
            _logger.LogInformation("project-registry-{Op} id={Id}", op, id);
            return updated;
        }
    }

    /// <summary>F45b hook: append a fully-formed record (used by the CRUD endpoints).</summary>
    internal ProjectRecord Append(ProjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!ProjIdFormat.IsMatch(record.Id))
            throw new ArgumentException($"Invalid project id format: {record.Id}", nameof(record));
        EnsureLoaded();
        lock (_gate)
        {
            if (_state.Projects.Any(p => string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Project id already exists: {record.Id}");
            _state = _state with { Projects = [.. _state.Projects, record] };
            PersistLocked();
            return record;
        }
    }

    /// <summary>F45b hook: reserve the next PROJ id without inserting a record.</summary>
    internal string AllocateNextId()
    {
        EnsureLoaded();
        lock (_gate)
        {
            var id = AllocateNextIdLocked();
            PersistLocked();
            return id;
        }
    }

    private string AllocateNextIdLocked()
    {
        var seq = _state.NextProjectIdSeq;
        // Defensive: in case the file was hand-edited and seq collides
        // with an existing record, walk forward until we find a free id.
        while (_state.Projects.Any(p => string.Equals(p.Id, FormatId(seq), StringComparison.OrdinalIgnoreCase)))
        {
            seq++;
        }
        var id = FormatId(seq);
        _state = _state with { NextProjectIdSeq = seq + 1 };
        return id;
    }

    private static string FormatId(int seq) => $"PROJ-{seq:D3}";

    private void PersistLocked()
    {
        if (_taskRepositoryRoot == null) return;
        try
        {
            Directory.CreateDirectory(RegistryPaths.MetadataDir(_taskRepositoryRoot));
            var path = RegistryPaths.ProjectsFilePath(_taskRepositoryRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "project-registry-persist-failed root={Root}", _taskRepositoryRoot);
            throw;
        }
    }

    private static string NormalisePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        return p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }
}
