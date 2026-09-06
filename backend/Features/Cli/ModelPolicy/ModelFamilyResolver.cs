using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>Stable family references used by runtime defaults.</summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string GptMini = "gpt-mini";
    public const string GptFlagship = "gpt-flagship";
}

/// <summary>
/// Resolves a model family to the newest available generation. Live CLI
/// catalogues win; the registry is the bounded fallback while discovery is
/// unavailable or stale. Discovery publishes snapshots here after every read,
/// so synchronous runtime defaults still resolve at call time.
/// </summary>
public static class ModelFamilyResolver
{
    internal static readonly TimeSpan LiveCatalogMaxAge = TimeSpan.FromHours(2);

    private static readonly ConcurrentDictionary<string, CliModelCatalog> LiveCatalogues =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Publish(string cliType, CliModelCatalog catalogue)
    {
        if (!string.IsNullOrWhiteSpace(cliType) && catalogue is not null)
            LiveCatalogues[CliTypes.Normalize(cliType)] = catalogue;
    }

    public static void ClearLiveCatalogues() => LiveCatalogues.Clear();

    public static string Resolve(string familyId, CliModelCatalog? catalogue = null)
    {
        var cliType = CliTypeFor(familyId);
        catalogue ??= LiveCatalogues.GetValueOrDefault(cliType);
        if (catalogue is not null && DateTime.UtcNow - catalogue.FetchedAt > LiveCatalogMaxAge)
            catalogue = null;
        var live = catalogue?.Models
            .Select((model, index) => (model, index))
            .Where(item => item.model.Available && !item.model.Deprecated && IsInFamily(familyId, item.model.Id))
            .OrderByDescending(item => Generation(item.model.Id))
            .ThenBy(item => item.index)
            .Select(item => item.model)
            .FirstOrDefault();
        if (live is not null) return ModelMetadataRegistry.NormalizeId(live.Id);

        var registry = ModelMetadataRegistry.All
            .Where(model => model.Available && !model.Deprecated && IsInFamily(familyId, model.Id))
            .OrderByDescending(model => Generation(model.Id))
            .FirstOrDefault();
        if (registry is not null) return registry.Id;

        throw new InvalidOperationException($"No available model is registered for family '{familyId}'.");
    }

    public static string? FamilyOf(string? modelId)
    {
        var id = ModelMetadataRegistry.NormalizeId(modelId);
        if (id.StartsWith("claude-haiku-", StringComparison.OrdinalIgnoreCase)) return ModelFamilies.ClaudeHaiku;
        if (id.StartsWith("claude-sonnet-", StringComparison.OrdinalIgnoreCase)) return ModelFamilies.ClaudeSonnet;
        if (id.StartsWith("claude-opus-", StringComparison.OrdinalIgnoreCase)) return ModelFamilies.ClaudeOpus;
        if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) && id.Contains("-mini", StringComparison.OrdinalIgnoreCase)) return ModelFamilies.GptMini;
        if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return ModelFamilies.GptFlagship;
        return null;
    }

    public static bool IsInFamily(string familyId, string? modelId)
        => string.Equals(FamilyOf(modelId), familyId, StringComparison.OrdinalIgnoreCase);

    public static long Generation(string? modelId)
    {
        var id = ModelMetadataRegistry.NormalizeId(modelId);
        var match = Regex.Match(id, @"(?<major>\d+)(?:[-.](?<minor>\d+))?(?:[-.](?<patch>\d+))?", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;
        static long Part(Group group) => long.TryParse(group.Value, out var value) ? value : 0;
        return Part(match.Groups["major"]) * 1_000_000
             + Part(match.Groups["minor"]) * 1_000
             + Part(match.Groups["patch"]);
    }

    private static string CliTypeFor(string familyId) => familyId switch
    {
        ModelFamilies.ClaudeHaiku or ModelFamilies.ClaudeSonnet or ModelFamilies.ClaudeOpus => CliTypes.Claude,
        ModelFamilies.GptMini or ModelFamilies.GptFlagship => CliTypes.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(familyId), familyId, "Unknown model family."),
    };
}
