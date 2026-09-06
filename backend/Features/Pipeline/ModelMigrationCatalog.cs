using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Registry;

namespace AgentStudio.Pipeline;

public sealed record ModelMigrationRule
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Family { get; init; } = "";
    public bool SafeAuto { get; init; }
    public string CostClassFrom { get; init; } = "";
    public string CostClassTo { get; init; } = "";
    public string ReasoningLadderFrom { get; init; } = "";
    public string ReasoningLadderTo { get; init; } = "";
    public string Rule { get; init; } = "";
}

public sealed record ModelMigrationDocument
{
    public string Version { get; init; } = "";
    public List<ModelMigrationRule> Rules { get; init; } = [];
}

public sealed record ModelMigrationProposal(
    string From,
    string To,
    string Family,
    bool SafeAuto,
    string CostClassFrom,
    string CostClassTo,
    string ReasoningLadderFrom,
    string ReasoningLadderTo,
    string Rule,
    string CatalogVersion);

/// <summary>
/// Cached adapter over Token Economy's versioned migration catalogue. A
/// configured path wins, followed by the registered Token Economy repository;
/// the embedded snapshot keeps admission deterministic when that project is
/// offline.
/// </summary>
public sealed class ModelMigrationCatalog
{
    private const string ResourceSuffix = "Policies.model-migrations.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IConfiguration _configuration;
    private readonly ProjectRegistry _projects;
    private readonly ILogger<ModelMigrationCatalog> _logger;
    private readonly object _gate = new();
    private ModelMigrationDocument? _cached;
    private string? _cachedPath;
    private DateTime _cachedWrite;

    public ModelMigrationCatalog(
        IConfiguration configuration,
        ProjectRegistry projects,
        ILogger<ModelMigrationCatalog> logger)
    {
        _configuration = configuration;
        _projects = projects;
        _logger = logger;
    }

    internal ModelMigrationCatalog(ModelMigrationDocument document)
    {
        _configuration = new ConfigurationBuilder().Build();
        _projects = null!;
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelMigrationCatalog>.Instance;
        _cached = Validate(document);
    }

    public ModelMigrationDocument Current
    {
        get
        {
            lock (_gate)
            {
                var path = ResolveExternalPath();
                var write = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_cached is not null && path == _cachedPath && write == _cachedWrite) return _cached;
                _cached = path is not null ? TryRead(path) : null;
                _cached ??= ReadEmbedded();
                _cachedPath = path;
                _cachedWrite = write;
                return _cached;
            }
        }
    }

    public ModelMigrationProposal? Propose(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var normalized = ModelMetadataRegistry.NormalizeId(model);
        var document = Current;
        var rule = document.Rules.FirstOrDefault(candidate =>
            string.Equals(ModelMetadataRegistry.NormalizeId(candidate.From), normalized, StringComparison.OrdinalIgnoreCase));
        return rule is null ? null : new ModelMigrationProposal(
            rule.From, rule.To, rule.Family, rule.SafeAuto,
            rule.CostClassFrom, rule.CostClassTo,
            rule.ReasoningLadderFrom, rule.ReasoningLadderTo,
            rule.Rule, document.Version);
    }

    private string? ResolveExternalPath()
    {
        var configured = _configuration["TokenEconomy:MigrationCatalogPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        if (_projects is null) return null;
        var project = _projects.List().FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, "Token Economy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.ShortCode, "TE", StringComparison.OrdinalIgnoreCase));
        var root = project?.RepositoryPath ?? project?.RootPath;
        if (string.IsNullOrWhiteSpace(root)) return null;
        return new[]
            {
                Path.Combine(root, "model-migrations.json"),
                Path.Combine(root, "data", "model-migrations.json"),
                Path.Combine(root, "config", "model-migrations.json"),
            }
            .FirstOrDefault(File.Exists);
    }

    private ModelMigrationDocument? TryRead(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<ModelMigrationDocument>(File.ReadAllText(path), JsonOptions);
            return document is null ? null : Validate(document);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token Economy model migration catalog could not be read from {Path}; using embedded snapshot", path);
            return null;
        }
    }

    private static ModelMigrationDocument ReadEmbedded()
    {
        var assembly = typeof(ModelMigrationCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return Validate(JsonSerializer.Deserialize<ModelMigrationDocument>(stream, JsonOptions)!);
    }

    private static ModelMigrationDocument Validate(ModelMigrationDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Version)) throw new InvalidOperationException("Migration catalog version is required.");
        if (document.Rules.Any(rule => string.IsNullOrWhiteSpace(rule.From)
                                       || string.IsNullOrWhiteSpace(rule.To)
                                       || string.IsNullOrWhiteSpace(rule.Rule)))
            throw new InvalidOperationException("Every migration rule requires from, to, and rule.");
        return document;
    }
}

public static class ModelMigrationAdmissionPolicy
{
    public static bool ShouldApply(bool modelExplicit, bool autoApplyEnabled, ModelMigrationProposal? proposal)
        => !modelExplicit && autoApplyEnabled && proposal?.SafeAuto == true;

    public static TimelineEvent CreateTimelineEvent(ModelMigrationProposal proposal)
        => new()
        {
            Ts = DateTime.UtcNow,
            Kind = TimelineEventKinds.ModelMigrated,
            Actor = TimelineActors.Orchestrator,
            Summary = $"Model migrated from {proposal.From} to {proposal.To}.",
            Details = new Dictionary<string, string>
            {
                ["from"] = proposal.From,
                ["to"] = proposal.To,
                ["rule"] = proposal.Rule,
                ["catalogVersion"] = proposal.CatalogVersion,
            },
        };
}

/// <summary>
/// Applies an operator-approved migration to one of the known supporting-agent
/// configuration pins. Pins are never changed by admission-time auto migration;
/// this explicit action persists the selected target in the active checkout's
/// local settings file and reloads configuration immediately.
/// </summary>
public sealed class ModelMigrationConfigurationPinService
{
    public static readonly IReadOnlyList<string> SupportedKeys =
    [
        "ClaudeCli:SummaryModel",
        "TitleGeneration:Model",
        "PromptEnhancement:Model",
        "WikiSearch:Model",
        "Supervisor:SoftReasoningModel",
        "ProposalManagement:Model",
        "ReviewDecisionOrchestrator:Model",
        "ReviewDecisionOrchestrator:AspectModel",
        "CodeReviewStep:DefaultModel",
        "TaskSpawnerStep:DefaultModel",
        "GlobalOrchestrator:Model",
        "CodexCli:DefaultModel",
        "CodexCli:Model",
    ];

    private static readonly object FileLock = new();
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ModelMigrationCatalog _catalog;
    private readonly ILogger<ModelMigrationConfigurationPinService> _logger;

    public ModelMigrationConfigurationPinService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ModelMigrationCatalog catalog,
        ILogger<ModelMigrationConfigurationPinService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _catalog = catalog;
        _logger = logger;
    }

    public IReadOnlyList<ModelMigrationConfigurationPin> GetProposals() => SupportedKeys
        .Select(key => new ModelMigrationConfigurationPin(key, _configuration[key], _catalog.Propose(_configuration[key])))
        .Where(pin => pin.Proposal is not null)
        .ToList();

    public ModelMigrationConfigurationPin Apply(string key)
    {
        var canonicalKey = SupportedKeys.FirstOrDefault(candidate =>
            string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
        if (canonicalKey is null) throw new ArgumentException($"Unsupported configuration pin '{key}'.", nameof(key));

        var proposal = _catalog.Propose(_configuration[canonicalKey])
            ?? throw new InvalidOperationException($"Configuration pin '{canonicalKey}' has no available migration.");

        lock (FileLock)
        {
            var path = Path.Combine(_environment.ContentRootPath, "appsettings.Local.json");
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            SetValue(root, canonicalKey, proposal.To);
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try { File.Replace(temp, path, destinationBackupFileName: null); }
            catch (FileNotFoundException) { File.Move(temp, path); }
            if (_configuration is IConfigurationRoot configurationRoot) configurationRoot.Reload();
            _logger.LogInformation(
                "Applied model migration {From} to {To} for configuration pin {Key} using catalog {CatalogVersion}",
                proposal.From, proposal.To, canonicalKey, proposal.CatalogVersion);
        }

        return new ModelMigrationConfigurationPin(canonicalKey, proposal.To, proposal);
    }

    private static void SetValue(JsonObject root, string key, string value)
    {
        var segments = key.Split(':');
        var cursor = root;
        foreach (var segment in segments[..^1])
        {
            if (cursor[segment] is not JsonObject child)
            {
                child = new JsonObject();
                cursor[segment] = child;
            }
            cursor = child;
        }
        cursor[segments[^1]] = value;
    }
}

public sealed record ModelMigrationConfigurationPin(
    string Key,
    string? Model,
    ModelMigrationProposal? Proposal);
