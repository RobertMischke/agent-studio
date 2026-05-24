using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Registry;

/// <summary>
/// F45a — in-memory cache plus persistence for the workspace catalog.
/// Workspaces are pure metadata (no folder per workspace); this service
/// reads and writes a single JSON file under
/// <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>.
///
/// <para>F45a ships read access plus the boot-time "ensure default
/// workspace" path. Mutations (create / rename / delete / reorder) ship
/// in F45b alongside the corresponding CRUD endpoints.</para>
///
/// <para>This service is registered as a singleton. <see cref="EnsureLoaded"/>
/// is idempotent; the constructor is cheap and does not touch disk.</para>
/// </summary>
public sealed class WorkspaceRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string? _taskRepositoryRoot;
    private readonly ILogger<WorkspaceRegistry> _logger;
    private readonly object _gate = new();
    private WorkspacesFile _state = new();
    private bool _loaded;

    public WorkspaceRegistry(IConfiguration config, ILogger<WorkspaceRegistry> logger)
    {
        _logger = logger;
        var raw = config["TaskRepository"];
        _taskRepositoryRoot = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>True when a backing TaskRepository is configured; false means in-memory-only operation.</summary>
    public bool IsPersistent => _taskRepositoryRoot != null;

    /// <summary>
    /// Loads the file on first call. Idempotent. If the file is missing
    /// or unreadable, leaves the registry empty so the caller can decide
    /// whether to seed defaults via <see cref="EnsureDefaultWorkspace"/>.
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            if (_taskRepositoryRoot == null) return;

            var path = RegistryPaths.WorkspacesFilePath(_taskRepositoryRoot);
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<WorkspacesFile>(json, JsonOpts);
                if (parsed != null)
                {
                    _state = parsed;
                    _logger.LogInformation(
                        "workspace-registry-loaded path={Path} count={Count}",
                        path, _state.Workspaces.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "workspace-registry-load-failed path={Path} - starting with empty list",
                    path);
            }
        }
    }

    /// <summary>
    /// Snapshot of the current workspaces, sorted by <see cref="WorkspaceRecord.SortOrder"/>
    /// then by display name. The returned list is a copy.
    /// </summary>
    public List<WorkspaceRecord> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Workspaces
                .OrderBy(w => w.SortOrder)
                .ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Returns the workspace with the given id, or null.</summary>
    public WorkspaceRecord? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Inserts <see cref="DefaultWorkspace"/> if no workspace exists yet
    /// and a TaskRepository is configured. Writes the file. Returns the
    /// default workspace record either way (so callers can rely on a
    /// non-null result post-boot).
    /// </summary>
    public WorkspaceRecord EnsureDefaultWorkspace(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        EnsureLoaded();
        lock (_gate)
        {
            var existing = _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, DefaultWorkspace.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var record = new WorkspaceRecord
            {
                Id = DefaultWorkspace.Id,
                DisplayName = DefaultWorkspace.DisplayName,
                SortOrder = 0,
                IsDefault = true,
                Color = DefaultWorkspace.Color,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            _state = _state with { Workspaces = [.. _state.Workspaces, record] };
            PersistLocked();
            _logger.LogInformation(
                "workspace-registry-default-seeded id={Id} displayName={DisplayName}",
                record.Id, record.DisplayName);
            return record;
        }
    }

    /// <summary>
    /// Replaces the in-memory state and writes it to disk. Internal hook
    /// used by F45b mutation endpoints; F45a keeps it internal so reads
    /// stay the only public surface.
    /// </summary>
    internal void Replace(WorkspacesFile next)
    {
        ArgumentNullException.ThrowIfNull(next);
        EnsureLoaded();
        lock (_gate)
        {
            _state = next;
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        if (_taskRepositoryRoot == null) return;
        try
        {
            Directory.CreateDirectory(RegistryPaths.MetadataDir(_taskRepositoryRoot));
            var path = RegistryPaths.WorkspacesFilePath(_taskRepositoryRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            // File.Move with overwrite is the atomic-ish replace on Windows.
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workspace-registry-persist-failed root={Root}", _taskRepositoryRoot);
            throw;
        }
    }
}
