using System.Collections.Concurrent;
using System.Text.Json;

namespace OrchestratorApi.Services.Drift;

/// <summary>
/// Tracking-state overrides for architecture-map elements. Drift reports are
/// append-only and immutable; the user nevertheless needs to mark an
/// element's drift Tracked / Accepted / Ignored without spawning a new
/// report. This store holds those overrides next to the drift pile in
/// <c>logs/drift/&lt;project&gt;/element-states.json</c> and applies them on
/// read when the project Drift surface renders the marble map.
/// </summary>
/// <remarks>
/// Overrides are scoped by <c>(workspace, project, modelId, elementId)</c> -
/// changing the architecture model in a future report does not silently
/// inherit a stale override. The state file is small (one record per element)
/// so a flat object keyed by <c>"{modelId}:{elementId}"</c> is enough.
/// </remarks>
public sealed class ArchitectureElementStateStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ElementStateOverride>> _cache = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public IReadOnlyDictionary<string, ElementStateOverride> Snapshot(string workspaceRoot, string project)
    {
        var key = CacheKey(workspaceRoot, project);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var loaded = Load(workspaceRoot, project);
        _cache[key] = loaded;
        return loaded;
    }

    public ElementStateOverride? Get(string workspaceRoot, string project, string modelId, string elementId)
    {
        var snap = Snapshot(workspaceRoot, project);
        return snap.TryGetValue(MakeKey(modelId, elementId), out var v) ? v : null;
    }

    public ElementStateOverride Set(
        string workspaceRoot,
        string project,
        string modelId,
        string elementId,
        DriftFindingStatus status,
        string? note,
        DateTime? updatedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        var record = new ElementStateOverride(
            ModelId: modelId,
            ElementId: elementId,
            Status: status,
            Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            UpdatedAt: updatedAtUtc ?? DateTime.UtcNow);
        var dir = DriftReportPaths.ProjectDir(workspaceRoot, project);
        Directory.CreateDirectory(dir);
        var path = StatePath(workspaceRoot, project);
        var snap = new Dictionary<string, ElementStateOverride>(Snapshot(workspaceRoot, project), StringComparer.Ordinal);
        snap[MakeKey(modelId, elementId)] = record;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(snap, JsonOptions));
        _cache[CacheKey(workspaceRoot, project)] = snap;
        return record;
    }

    public void InvalidateCache(string workspaceRoot, string project)
    {
        _cache.TryRemove(CacheKey(workspaceRoot, project), out _);
    }

    private static IReadOnlyDictionary<string, ElementStateOverride> Load(string workspaceRoot, string project)
    {
        var path = StatePath(workspaceRoot, project);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ElementStateOverride>(StringComparer.Ordinal);
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ElementStateOverride>>(bytes, JsonOptions);
            return parsed ?? new Dictionary<string, ElementStateOverride>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupt file: fall back to empty so the marble surface still
            // renders the report's own status values; the user can re-mark.
            return new Dictionary<string, ElementStateOverride>(StringComparer.Ordinal);
        }
    }

    private static string StatePath(string workspaceRoot, string project) =>
        Path.Combine(DriftReportPaths.ProjectDir(workspaceRoot, project), "element-states.json");

    private static string CacheKey(string workspaceRoot, string project) =>
        string.Concat(workspaceRoot, "::", project);

    private static string MakeKey(string modelId, string elementId) =>
        string.Concat(modelId, ":", elementId);
}

public sealed record ElementStateOverride(
    string ModelId,
    string ElementId,
    DriftFindingStatus Status,
    string? Note,
    DateTime UpdatedAt);
