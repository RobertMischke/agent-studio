using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

/// <summary>
/// Per-project preferences that persist across restarts (auto-commit toggle today;
/// future per-project flags fit here too). Stored as a single JSON map next to
/// the task repository; falls back to LocalAppData when the repository path is
/// not configured so the file always lives on a writable disk.
/// </summary>
public class ProjectSettingsService
{
    private readonly ILogger<ProjectSettingsService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private Dictionary<string, ProjectSettings> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ProjectSettingsService(ILogger<ProjectSettingsService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public ProjectSettings Get(string projectName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
        }
    }

    public Dictionary<string, ProjectSettings> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return new Dictionary<string, ProjectSettings>(_cache, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetAutoCommit(string projectName, bool enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { AutoCommit = enabled };
            Persist();
        }
    }

    /// <summary>
    /// Persists the runner mode for a project so the auto-pickup toggle survives
    /// a backend restart. Null clears the persisted value (revert to default).
    /// </summary>
    public void SetRunnerMode(string projectName, string? mode)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { RunnerMode = mode };
            Persist();
        }
    }

    /// <summary>
    /// Sets the model the orchestrator uses when deciding on the user's
    /// behalf in auto mode. Null clears (revert to default Opus).
    /// </summary>
    public void SetOrchestratorModel(string projectName, string? model)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { OrchestratorModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim() };
            Persist();
        }
    }

    /// <summary>
    /// Sets the cadence for one analysis-report topic on this project.
    /// Cadences are validated by the caller; null or empty value clears the
    /// entry (revert to "disabled" default). Every project starts with no
    /// schedules so reports never auto-run without an explicit opt-in.
    /// </summary>
    public void SetAnalysisSchedule(string projectName, string topic, string? cadence)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var map = current.AnalysisSchedules is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.AnalysisSchedules, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cadence))
            {
                map.Remove(topic.Trim());
            }
            else
            {
                map[topic.Trim()] = cadence.Trim();
            }
            _cache[projectName] = current with { AnalysisSchedules = map };
            Persist();
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolveStorePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<Dictionary<string, ProjectSettings>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc != null)
                    _cache = new Dictionary<string, ProjectSettings>(doc, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read project-settings.json — starting with defaults");
            }
        }
    }

    private void Persist()
    {
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write project-settings.json at {Path}", path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(taskRepo, "project-settings.json");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", "project-settings.json");
    }
}
