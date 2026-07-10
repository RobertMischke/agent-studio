using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Workspace-wide primary/fallback routing for CLI models. The persisted
/// profile is advisory until the primary CLI reaches a configured quota cap;
/// then the fallback is selected for that run only. No task metadata is
/// rewritten, so the next run returns to primary as soon as the snapshot is
/// below the cap again.
/// </summary>
public sealed class CliQuotaFallbackService
{
    private const string FileName = "cli-model-routing.json";
    private readonly IConfiguration _config;
    private readonly ILogger<CliQuotaFallbackService> _logger;
    private readonly object _lock = new();
    private Dictionary<string, CliModelRouteProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaFallbackService(IConfiguration config, ILogger<CliQuotaFallbackService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, CliModelRouteProfile> GetAll()
    {
        EnsureLoaded();
        lock (_lock) return new Dictionary<string, CliModelRouteProfile>(_profiles, StringComparer.OrdinalIgnoreCase);
    }

    public CliModelRouteProfile Set(CliModelRouteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CliType)) throw new ArgumentException("cliType is required");
        var normalized = profile with
        {
            CliType = profile.CliType.Trim().ToLowerInvariant(),
            PrimaryModel = Clean(profile.PrimaryModel),
            PrimaryThinkingLevel = Clean(profile.PrimaryThinkingLevel),
            FallbackCliType = Clean(profile.FallbackCliType)?.ToLowerInvariant(),
            FallbackModel = Clean(profile.FallbackModel),
            FallbackThinkingLevel = Clean(profile.FallbackThinkingLevel),
        };
        EnsureLoaded();
        lock (_lock)
        {
            _profiles[normalized.CliType] = normalized;
            Persist();
        }
        _logger.LogInformation(
            "cli_quota_fallback_configured primaryCli={PrimaryCli} primaryModel={PrimaryModel} fallbackCli={FallbackCli} fallbackModel={FallbackModel}",
            normalized.CliType, normalized.PrimaryModel ?? "<cli-default>",
            normalized.FallbackCliType ?? normalized.CliType, normalized.FallbackModel ?? "<disabled>");
        return normalized;
    }

    public CliRouteDecision Resolve(
        string? requestedCliType,
        string? requestedModel,
        string? requestedThinkingLevel,
        Func<string?, CapEvaluation> evaluateQuota)
    {
        var cli = Clean(requestedCliType)?.ToLowerInvariant() ?? CliTypes.Claude;
        EnsureLoaded();
        CliModelRouteProfile? profile;
        lock (_lock) _profiles.TryGetValue(cli, out profile);

        var primaryModel = Clean(requestedModel) ?? profile?.PrimaryModel;
        var primaryThinking = Clean(requestedThinkingLevel) ?? profile?.PrimaryThinkingLevel;
        var cap = evaluateQuota(cli);
        if (!cap.Blocked)
            return new(cli, primaryModel, primaryThinking, false, null, cap);

        if (profile == null || string.IsNullOrWhiteSpace(profile.FallbackModel))
            return new(cli, primaryModel, primaryThinking, false, cap.DescribeReason(), cap);

        var fallbackCli = profile.FallbackCliType ?? cli;
        var fallbackCap = string.Equals(fallbackCli, cli, StringComparison.OrdinalIgnoreCase)
            ? CapEvaluation.NotBlocked
            : evaluateQuota(fallbackCli);
        if (fallbackCap.Blocked)
            return new(cli, primaryModel, primaryThinking, false,
                $"primary {cap.DescribeReason()}; fallback {fallbackCap.DescribeReason()}", cap);

        return new(
            fallbackCli,
            profile.FallbackModel,
            profile.FallbackThinkingLevel,
            true,
            cap.DescribeReason(),
            cap);
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolvePath();
            if (!File.Exists(path)) return;
            try
            {
                var profiles = JsonSerializer.Deserialize<List<CliModelRouteProfile>>(
                    File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                _profiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p.CliType))
                    .ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to read {File}", path); }
        }
    }

    private void Persist()
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_profiles.Values.OrderBy(p => p.CliType),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to write {File}", path); }
    }

    private string ResolvePath()
    {
        var root = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-taskboard");
        return Path.Combine(root, FileName);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CliModelRouteProfile
{
    public string CliType { get; init; } = "";
    public string? PrimaryModel { get; init; }
    public string? PrimaryThinkingLevel { get; init; }
    public string? FallbackCliType { get; init; }
    public string? FallbackModel { get; init; }
    public string? FallbackThinkingLevel { get; init; }
}

public sealed record CliRouteDecision(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string? Reason,
    CapEvaluation PrimaryCap);
