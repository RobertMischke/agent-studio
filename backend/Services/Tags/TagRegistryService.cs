using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Tags;

/// <summary>
/// Workspace-level tag registry. Tags are a flat namespace shared across the
/// watched projects in one workspace and stored as a single JSON array at
/// <c>&lt;TaskRepository&gt;/tags.json</c>. The file is seeded with three
/// default tags (architecture, performance, quality) on first read so a
/// fresh workspace already has something to attach.
/// </summary>
/// <remarks>
/// Concurrency: a process-wide lock protects the in-memory cache and the
/// disk write. Writes are read-modify-write because adding a tag must
/// reject duplicate ids and the registry is rarely written compared to read.
/// </remarks>
public sealed class TagRegistryService
{
    private const string FileName = "tags.json";
    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    private static readonly TagRegistryEntry[] Seed =
    [
        new() { Id = "architecture", Label = "Architecture", Color = "#89b4fa", Description = "" },
        new() { Id = "performance",  Label = "Performance",  Color = "#fab387", Description = "" },
        new() { Id = "quality",      Label = "Quality",      Color = "#a6e3a1", Description = "" }
    ];

    private readonly ILogger<TagRegistryService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private List<TagRegistryEntry>? _cache;

    public TagRegistryService(ILogger<TagRegistryService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public IReadOnlyList<TagRegistryEntry> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache!.Select(Clone).ToList();
        }
    }

    /// <summary>
    /// Add a new tag. When <paramref name="id"/> is empty, derive it from
    /// <paramref name="label"/>. Returns the stored entry on success,
    /// throws <see cref="InvalidOperationException"/> on duplicate id, and
    /// throws <see cref="ArgumentException"/> on an invalid id or empty
    /// label.
    /// </summary>
    public TagRegistryEntry Create(string? id, string label, string? color, string? description)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required");

        var resolvedId = string.IsNullOrWhiteSpace(id)
            ? JobMutationService.NormalizeTagId(label)
            : JobMutationService.NormalizeTagId(id);

        if (!IdPattern.IsMatch(resolvedId))
            throw new ArgumentException($"Invalid tag id '{resolvedId}'. Allowed: [a-z0-9-]{{1,32}}.");

        EnsureLoaded();
        lock (_lock)
        {
            if (_cache!.Any(t => string.Equals(t.Id, resolvedId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Tag '{resolvedId}' already exists");

            var entry = new TagRegistryEntry
            {
                Id = resolvedId,
                Label = label.Trim(),
                Color = NormalizeColor(color),
                Description = description?.Trim() ?? string.Empty
            };
            _cache!.Add(entry);
            Persist();
            return Clone(entry);
        }
    }

    /// <summary>
    /// Soft-delete: drop the registry entry. Per-job tag arrays are NOT
    /// rewritten; the FE renders unknown ids as a faint ghost chip until
    /// the user re-tags.
    /// </summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        EnsureLoaded();
        lock (_lock)
        {
            var idx = _cache!.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _cache.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    public bool Exists(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        EnsureLoaded();
        lock (_lock)
        {
            return _cache!.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_cache != null) return;
            var path = ResolveStorePath();
            if (path == null)
            {
                _cache = Seed.Select(Clone).ToList();
                return;
            }

            if (!File.Exists(path))
            {
                _cache = Seed.Select(Clone).ToList();
                Persist();
                _logger.LogInformation("Seeded tag registry at {Path} with {Count} default tags", path, _cache.Count);
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<List<TagRegistryEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _cache = doc?.Where(t => !string.IsNullOrWhiteSpace(t.Id) && IdPattern.IsMatch(t.Id))
                              .ToList()
                          ?? Seed.Select(Clone).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read tag registry at {Path}; using defaults", path);
                _cache = Seed.Select(Clone).ToList();
            }
        }
    }

    private void Persist()
    {
        var path = ResolveStorePath();
        if (path == null || _cache == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(_cache,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist tag registry to {Path}", path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(taskRepo, FileName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", FileName);
    }

    private static string NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "#94a3b8";
        var s = raw.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        // Accept #rgb, #rrggbb, #rrggbbaa loosely; otherwise default.
        return Regex.IsMatch(s, "^#[0-9a-fA-F]{3,8}$") ? s : "#94a3b8";
    }

    private static TagRegistryEntry Clone(TagRegistryEntry e) => new()
    {
        Id = e.Id,
        Label = e.Label,
        Color = e.Color,
        Description = e.Description
    };
}
