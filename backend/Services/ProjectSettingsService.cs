using System.Text.Json;
using System.Text.RegularExpressions;
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

    public void SetAutoPushStrategy(string projectName, string strategy)
    {
        EnsureLoaded();
        var normalized = AutoPushStrategies.Normalize(strategy);
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { AutoPushStrategy = normalized };
            Persist();
        }
    }

    /// <summary>
    /// ADR-0052: sets the max number of tasks the runner may run concurrently
    /// for this project. Clamped to <c>&gt;= 1</c>; <c>1</c> keeps the runner
    /// sequential.
    /// </summary>
    public void SetMaxParallelism(string projectName, int maxParallelism)
    {
        EnsureLoaded();
        var clamped = maxParallelism < 1 ? 1 : maxParallelism;
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { MaxParallelism = clamped };
            Persist();
        }
        _logger.LogInformation("Max parallelism set to {Max} for project {Project}", clamped, projectName);
    }

    /// <summary>
    /// ADR-0052: sets the integration branch parallel task worktrees branch off
    /// and merge back into. Blank reverts to the default (<c>develop</c>).
    /// </summary>
    public void SetIntegrationBranch(string projectName, string? branch)
    {
        EnsureLoaded();
        var value = string.IsNullOrWhiteSpace(branch) ? new ProjectSettings().IntegrationBranch : branch.Trim();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { IntegrationBranch = value };
            Persist();
        }
        _logger.LogInformation("Integration branch set to {Branch} for project {Project}", value, projectName);
    }

    /// <summary>
    /// ADR-0052: sets how a finished task branch is folded back into the
    /// integration branch. Unknown values normalize to <c>direct-merge</c>.
    /// </summary>
    public void SetIntegrationStrategy(string projectName, string strategy)
    {
        EnsureLoaded();
        var normalized = IntegrationStrategies.Normalize(strategy);
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { IntegrationStrategy = normalized };
            Persist();
        }
        _logger.LogInformation("Integration strategy set to {Strategy} for project {Project}", normalized, projectName);
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
    public void SetOrchestratorModel(string projectName, string? model, string? thinkingLevel = null)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            _cache[projectName] = current with
            {
                OrchestratorModel = normalizedModel,
                OrchestratorThinkingLevel = thinkingLevel is null
                    ? current.OrchestratorThinkingLevel
                    : (string.IsNullOrWhiteSpace(thinkingLevel)
                        ? null
                        : CliThinkingLevels.Normalize(CliTypes.Claude, normalizedModel, thinkingLevel))
            };
            Persist();
        }
    }

    /// <summary>
    /// Tunes the epic decomposition (planning) run for a project. A null
    /// argument leaves that knob untouched, so the caller can set the model
    /// and the backlog/ready target independently. An empty model string
    /// clears the override (revert to the epic card's own model).
    /// </summary>
    public void SetEpicPlanning(string projectName, string? model, string? thinkingLevel, bool? subTasksToReady)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var normalizedModel = model is null
                ? current.EpicPlanningModel
                : (string.IsNullOrWhiteSpace(model) ? null : model.Trim());
            _cache[projectName] = current with
            {
                EpicPlanningModel = normalizedModel,
                EpicPlanningThinkingLevel = thinkingLevel is null
                    ? current.EpicPlanningThinkingLevel
                    : (string.IsNullOrWhiteSpace(thinkingLevel)
                        ? null
                        : CliThinkingLevels.Normalize(CliTypes.Claude, normalizedModel, thinkingLevel)),
                EpicSubTasksToReady = subTasksToReady ?? current.EpicSubTasksToReady,
            };
            Persist();
        }
        _logger.LogInformation(
            "Epic planning settings updated for project {Project} (model={Model}, subTasksToReady={ToReady})",
            projectName, model, subTasksToReady);
    }

    /// <summary>
    /// ADR-0026: sets the per-project autonomy level for the
    /// orchestrator-prep loop. Accepts <c>0..4</c>; out-of-range values are
    /// clamped to the nearest valid stop. Null clears (revert to the default
    /// balanced level when the setting is read).
    /// </summary>
    /// <summary>
    /// Per-project toggle for the orchestrator-intake loop. When enabled, the
    /// coding runner stops picking up 2-ready cards until intake has marked
    /// them as <c>phase == intake-passed</c>. Default is disabled (null is
    /// treated as false at the read site).
    /// </summary>
    public void SetIntakeEnabled(string projectName, bool? enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { IntakeEnabled = enabled };
            Persist();
        }
        _logger.LogInformation("Intake enabled set to {Enabled} for project {Project}", enabled, projectName);
    }

    public void SetAutonomyLevel(string projectName, int? level)
    {
        EnsureLoaded();
        int? clamped = level is null ? null : Math.Clamp(level.Value, 0, 4);
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            _cache[projectName] = current with { AutonomyLevel = clamped };
            Persist();
        }
        _logger.LogInformation("Autonomy level set to {Level} for project {Project}", clamped, projectName);
    }

    /// <summary>
    /// Sets the cadence for one analysis-report topic on this project.
    /// Cadences are validated by the caller; null or empty value clears the
    /// entry (revert to "disabled" default). Every project starts with no
    /// schedules so reports never auto-run without an explicit opt-in.
    /// </summary>
    /// <summary>
    /// F35: writes the sort strategy override for a single lane. A null or
    /// empty <paramref name="strategy"/> clears the override (the lane
    /// reverts to <see cref="LaneSortStrategies.GetDefaultForLane"/>).
    /// Invalid strategy ids are rejected by the caller; this method
    /// normalises to ensure only canonical ids land on disk.
    /// </summary>
    public void SetLaneSortStrategy(string projectName, string lane, string? strategy)
    {
        if (string.IsNullOrWhiteSpace(lane)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var map = current.LaneSortStrategyOverrides is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.LaneSortStrategyOverrides, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(strategy))
            {
                map.Remove(lane.Trim());
            }
            else
            {
                map[lane.Trim()] = LaneSortStrategies.Normalize(strategy);
            }
            _cache[projectName] = current with { LaneSortStrategyOverrides = map.Count == 0 ? null : map };
            Persist();
        }
    }

    /// <summary>
    /// Upsert the per-project override for one pipeline step
    /// (<see cref="ProjectSettings.PipelineSteps"/>). Null fields inside the
    /// supplied <paramref name="setting"/> stay null (no override on that
    /// dimension). Passing a null <paramref name="setting"/>, or one whose
    /// every field is null, removes the entry so the step reverts to its
    /// built-in defaults.
    /// </summary>
    public void SetPipelineStep(string projectName, string stepId, PipelineStepSetting? setting)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var map = current.PipelineSteps is null
                ? new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PipelineStepSetting>(current.PipelineSteps, StringComparer.OrdinalIgnoreCase);

            var normalizedCliType = string.IsNullOrWhiteSpace(setting?.CliType)
                ? null
                : setting!.CliType!.Trim().ToLowerInvariant();
            var normalizedModel = string.IsNullOrWhiteSpace(setting?.Model) ? null : setting!.Model!.Trim();
            var normalizedThinkingLevel = string.IsNullOrWhiteSpace(setting?.ThinkingLevel)
                ? null
                : setting!.ThinkingLevel!.Trim().ToLowerInvariant();
            var normalizedMode = string.IsNullOrWhiteSpace(setting?.Mode) ? null : setting!.Mode!.Trim().ToLowerInvariant();
            var normalizedPrompt = string.IsNullOrWhiteSpace(setting?.Prompt) ? null : setting!.Prompt!.Trim();
            var normalizedCondition = NormalizeCondition(setting?.Condition);
            var isEmpty = setting is null
                || (setting.Enabled is null && normalizedMode is null && normalizedCliType is null && normalizedModel is null && normalizedThinkingLevel is null && normalizedPrompt is null && normalizedCondition is null);

            if (isEmpty)
            {
                map.Remove(stepId.Trim());
            }
            else
            {
                map[stepId.Trim()] = new PipelineStepSetting
                {
                    Enabled = setting!.Enabled,
                    Mode = normalizedMode,
                    CliType = normalizedCliType,
                    Model = normalizedModel,
                    ThinkingLevel = normalizedThinkingLevel,
                    Prompt = normalizedPrompt,
                    Condition = normalizedCondition,
                };
            }
            _cache[projectName] = current with { PipelineSteps = map.Count == 0 ? null : map };
            Persist();
        }
        _logger.LogInformation("Pipeline step '{StepId}' config updated for project {Project}", stepId, projectName);
    }

    /// <summary>
    /// Canonicalize a step condition for storage. A null/blank/unknown token,
    /// or an explicit <see cref="PipelineStepConditions.Always"/>, collapses to
    /// null ("no override, always run"). Value-bearing tokens keep a trimmed
    /// value; a value-bearing token with no value also collapses to null since
    /// it can never match.
    /// </summary>
    private static PipelineStepCondition? NormalizeCondition(PipelineStepCondition? condition)
    {
        var when = PipelineStepConditions.Normalize(condition?.When);
        if (when is null || when == PipelineStepConditions.Always) return null;

        var value = string.IsNullOrWhiteSpace(condition?.Value) ? null : condition!.Value!.Trim();
        if (PipelineStepConditions.RequiresValue(when) && value is null) return null;

        return new PipelineStepCondition { When = when, Value = PipelineStepConditions.RequiresValue(when) ? value : null };
    }

    /// <summary>
    /// Sets the per-project permission mode for one CLI
    /// (<see cref="ProjectSettings.CliModes"/>). A null / empty / unknown
    /// <paramref name="mode"/> clears the override so the CLI reverts to the
    /// platform default (YOLO) / global config. Invalid CLI ids are ignored.
    /// </summary>
    public void SetCliMode(string projectName, string cliType, string? mode)
    {
        if (!CliTypes.IsValid(cliType)) return;
        EnsureLoaded();
        var cli = CliTypes.Normalize(cliType);
        lock (_lock)
        {
            var current = _cache.TryGetValue(projectName, out var s) ? s : new ProjectSettings();
            var map = current.CliModes is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.CliModes, StringComparer.OrdinalIgnoreCase);

            // Empty/unknown mode clears the override (revert to default). A valid
            // mode is stored canonically so only known ids ever land on disk.
            if (string.IsNullOrWhiteSpace(mode) || !CliPermissionModes.IsValid(mode))
                map.Remove(cli);
            else
                map[cli] = CliPermissionModes.Normalize(mode);

            _cache[projectName] = current with { CliModes = map.Count == 0 ? null : map };
            Persist();
        }
        _logger.LogInformation("CLI permission mode for {Cli} set to {Mode} for project {Project}",
            cli, string.IsNullOrWhiteSpace(mode) ? "(default)" : CliPermissionModes.Normalize(mode), projectName);
    }

    /// <summary>
    /// Resolves the effective permission mode for one CLI in one project.
    /// Order: explicit per-project override → detected global CLI config →
    /// platform default (YOLO). The returned resolution carries the concrete
    /// flags the driver will inject, so callers (probe endpoint, UI) can show
    /// exactly what a spawn would do.
    /// </summary>
    public CliPermissionResolution ResolveCliMode(string projectName, string? cliType)
    {
        var cli = CliTypes.Normalize(cliType);
        var settings = Get(projectName);

        if (settings.CliModes != null
            && settings.CliModes.TryGetValue(cli, out var configured)
            && CliPermissionModes.IsValid(configured))
        {
            var mode = CliPermissionModes.Normalize(configured);
            return new CliPermissionResolution
            {
                CliType = cli,
                Mode = mode,
                Source = CliPermissionSources.Project,
                Args = CliPermissionFlags.For(cli, mode),
            };
        }

        var global = TryDetectGlobalMode(cli);
        if (global != null)
        {
            return new CliPermissionResolution
            {
                CliType = cli,
                Mode = global,
                Source = CliPermissionSources.Global,
                Args = CliPermissionFlags.For(cli, global),
            };
        }

        return new CliPermissionResolution
        {
            CliType = cli,
            Mode = CliPermissionModes.Yolo,
            Source = CliPermissionSources.Default,
            Args = CliPermissionFlags.For(cli, CliPermissionModes.Yolo),
        };
    }

    private static readonly Regex CodexSandboxModeRegex = new(
        "sandbox_mode\\s*=\\s*\"(?<mode>[a-z-]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Best-effort detection of a CLI's persisted global permission mode.
    /// Only Codex stores a parseable mode (<c>sandbox_mode</c> in
    /// <c>~/.codex/config.toml</c>); the other CLIs keep no comparable
    /// file-based posture, so they return null and resolve to the default.
    /// Returns null when nothing is detected.
    /// </summary>
    private static string? TryDetectGlobalMode(string cli)
    {
        if (!string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            var configPath = Path.Combine(home, ".codex", "config.toml");
            if (!File.Exists(configPath)) return null;

            var match = CodexSandboxModeRegex.Match(File.ReadAllText(configPath));
            if (!match.Success) return null;

            return match.Groups["mode"].Value.ToLowerInvariant() switch
            {
                "danger-full-access" => CliPermissionModes.Yolo,
                "workspace-write" => CliPermissionModes.WorkspaceWrite,
                "read-only" => CliPermissionModes.ReadOnly,
                _ => null,
            };
        }
        catch
        {
            // A missing / unreadable / malformed global config is not an error:
            // we simply fall through to the platform default.
            return null;
        }
    }

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
