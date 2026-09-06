using System.Text.Json;

namespace AgentStudio.Cli;

public sealed record ModelMigrationRule
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Family { get; init; } = "";
    public string Rule { get; init; } = "";
    public bool SafeAuto { get; init; }
    public string FromCostClass { get; init; } = "unknown";
    public string ToCostClass { get; init; } = "unknown";
    public string[] FromReasoningLadder { get; init; } = [];
    public string[] ToReasoningLadder { get; init; } = [];
}

public sealed record ModelMigrationCatalog
{
    public string Version { get; init; } = "unavailable";
    public List<ModelMigrationRule> Migrations { get; init; } = [];
}

public sealed record ModelMigrationProposal(
    string From,
    string To,
    string Family,
    string Rule,
    bool SafeAuto,
    string CatalogVersion,
    string FromCostClass,
    string ToCostClass,
    IReadOnlyList<string> FromReasoningLadder,
    IReadOnlyList<string> ToReasoningLadder);

/// <summary>
/// Cached adapter over Token Economy's versioned model migration JSON. The
/// registered Token Economy repository is the default source; tests and remote
/// installations can provide <c>TokenEconomy:ModelMigrationCatalogPath</c>.
/// </summary>
public sealed class ModelMigrationCatalogService
{
    public const string RelativeCatalogPath = "src/TokenEconomy/catalog/model-migrations.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfiguration _configuration;
    private readonly AgentStudio.Registry.ProjectRegistry? _projects;
    private readonly ILogger<ModelMigrationCatalogService> _logger;
    private readonly object _gate = new();
    private ModelMigrationCatalog? _cached;
    private string? _cachedPath;
    private DateTime _cachedWriteTime;

    public ModelMigrationCatalogService(
        IConfiguration configuration,
        ILogger<ModelMigrationCatalogService> logger,
        AgentStudio.Registry.ProjectRegistry? projects = null)
    {
        _configuration = configuration;
        _logger = logger;
        _projects = projects;
    }

    public ModelMigrationCatalog GetCatalog()
    {
        var path = ResolvePath();
        var writeTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        lock (_gate)
        {
            if (_cached is not null
                && string.Equals(path, _cachedPath, StringComparison.OrdinalIgnoreCase)
                && writeTime == _cachedWriteTime)
                return _cached;

            _cachedPath = path;
            _cachedWriteTime = writeTime;
            _cached = TryRead(path) ?? BuiltInFallback();
            return _cached;
        }
    }

    public ModelMigrationProposal? Propose(string? modelId)
    {
        var normalized = ModelMetadataRegistry.NormalizeId(modelId);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var catalog = GetCatalog();
        var rule = catalog.Migrations.FirstOrDefault(candidate =>
            string.Equals(ModelMetadataRegistry.NormalizeId(candidate.From), normalized, StringComparison.OrdinalIgnoreCase));
        if (rule is null || string.Equals(rule.From, rule.To, StringComparison.OrdinalIgnoreCase)) return null;
        return new ModelMigrationProposal(
            normalized,
            ModelMetadataRegistry.NormalizeId(rule.To),
            rule.Family,
            rule.Rule,
            rule.SafeAuto,
            catalog.Version,
            rule.FromCostClass,
            rule.ToCostClass,
            rule.FromReasoningLadder,
            rule.ToReasoningLadder);
    }

    public ModelMigrationProposal? SafeAutomaticMigration(string? modelId, bool isExplicit, bool autoApplyEnabled)
    {
        if (isExplicit || !autoApplyEnabled) return null;
        var proposal = Propose(modelId);
        if (proposal is not { SafeAuto: true }) return null;
        if (!string.Equals(ModelFamilyResolver.FamilyOf(proposal.From), ModelFamilyResolver.FamilyOf(proposal.To), StringComparison.OrdinalIgnoreCase))
            return null;
        return proposal;
    }

    private ModelMigrationCatalog? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ModelMigrationCatalog>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token Economy model migration catalog could not be read at {Path}; using the bundled compatibility catalog", path);
            return null;
        }
    }

    private string? ResolvePath()
    {
        var configured = _configuration["TokenEconomy:ModelMigrationCatalogPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var project = _projects?.List().FirstOrDefault(candidate =>
            string.Equals(candidate.ShortCode, "TE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, "Token Economy", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(project?.RepositoryPath)
            ? null
            : Path.Combine(project.RepositoryPath, RelativeCatalogPath);
    }

    private static ModelMigrationCatalog BuiltInFallback() => new()
    {
        Version = "studio-fallback-2026-09-06",
        Migrations =
        [
            Rule(ModelIds.ClaudeOpus48, ModelIds.ClaudeOpus5, ModelFamilies.ClaudeOpus, "latest-in-family", true, "premium", "premium"),
            Rule(ModelIds.ClaudeOpus47, ModelIds.ClaudeOpus5, ModelFamilies.ClaudeOpus, "latest-in-family", true, "premium", "premium"),
            Rule(ModelIds.ClaudeOpus46, ModelIds.ClaudeOpus5, ModelFamilies.ClaudeOpus, "latest-in-family", true, "premium", "premium"),
            Rule(ModelIds.ClaudeOpus45, ModelIds.ClaudeOpus5, ModelFamilies.ClaudeOpus, "latest-in-family", true, "premium", "premium"),
            Rule(ModelIds.ClaudeSonnet46, ModelIds.ClaudeSonnet5, ModelFamilies.ClaudeSonnet, "latest-in-family", true, "standard", "standard"),
            Rule(ModelIds.ClaudeSonnet45, ModelIds.ClaudeSonnet5, ModelFamilies.ClaudeSonnet, "latest-in-family", true, "standard", "standard"),
            Rule(ModelIds.ClaudeHaiku45, ModelIds.ClaudeSonnet5, ModelFamilies.ClaudeHaiku, "te-economy-haiku-to-sonnet-5", false, "economy", "standard"),
        ],
    };

    private static ModelMigrationRule Rule(
        string from,
        string to,
        string family,
        string rule,
        bool safeAuto,
        string fromCost,
        string toCost) => new()
    {
        From = from,
        To = to,
        Family = family,
        Rule = rule,
        SafeAuto = safeAuto,
        FromCostClass = fromCost,
        ToCostClass = toCost,
        FromReasoningLadder = CliThinkingLevels.For(CliType(from), from).ToArray(),
        ToReasoningLadder = CliThinkingLevels.For(CliType(to), to).ToArray(),
    };

    private static string CliType(string model) => model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
        ? CliTypes.Claude
        : CliTypes.Codex;
}

public static class ModelMigrationAudit
{
    public static TimelineEvent TimelineEvent(ModelMigrationProposal migration, DateTime? at = null) => new()
    {
        Ts = at ?? DateTime.UtcNow,
        Kind = TimelineEventKinds.ModelMigrated,
        Actor = TimelineActors.Orchestrator,
        Summary = $"Model migrated from {migration.From} to {migration.To}.",
        Details = new Dictionary<string, string>
        {
            ["from"] = migration.From,
            ["to"] = migration.To,
            ["rule"] = migration.Rule,
            ["catalogVersion"] = migration.CatalogVersion,
        },
    };
}
