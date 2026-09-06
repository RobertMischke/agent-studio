using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>Stable model-family ids used by defaults that must follow CLI releases.</summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string GptMini = "gpt-mini";
    public const string GptFlagship = "gpt-flagship";

    public static readonly IReadOnlyList<string> All =
        [ClaudeHaiku, ClaudeSonnet, ClaudeOpus, GptMini, GptFlagship];
}

/// <summary>
/// Resolves a stable family to the newest available concrete model. Discovery
/// publishes each live catalogue here, so synchronous supporting-agent defaults
/// follow the installed CLI without retaining a concrete release id. A registry
/// catalogue is used until discovery succeeds and whenever its cached result is
/// stale or unavailable.
/// </summary>
public sealed class ModelFamilyResolver
{
    private static readonly ConcurrentDictionary<string, CliModelCatalog> LiveCatalogues =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionPart = new(@"\d+", RegexOptions.Compiled);
    private static readonly TimeSpan MaxLiveCatalogAge = TimeSpan.FromHours(24);

    public string Resolve(string family) => ResolveAvailable(family);

    public static void Publish(string cliType, CliModelCatalog catalogue)
    {
        if (!CliTypes.IsValid(cliType) || catalogue.Models.Count == 0) return;
        LiveCatalogues[CliTypes.Normalize(cliType)] = catalogue;
    }

    internal static void ClearPublishedCatalogues() => LiveCatalogues.Clear();

    public static string ResolveAvailable(string family)
    {
        var cliType = CliTypeFor(family);
        LiveCatalogues.TryGetValue(cliType, out var live);
        return Resolve(family, live);
    }

    /// <summary>Pure catalogue overload used by captured-catalogue tests.</summary>
    public static string Resolve(string family, CliModelCatalog? liveCatalogue)
    {
        if (!ModelFamilies.All.Contains(family, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown model family.");

        var registry = RegistryCandidates(family).ToList();
        var liveIsFresh = liveCatalogue is not null
                          && liveCatalogue.Models.Count > 0
                          && liveCatalogue.FetchedAt >= DateTime.UtcNow - MaxLiveCatalogAge
                          && !IsFallbackSource(liveCatalogue.Source);
        var candidates = liveIsFresh
            ? liveCatalogue!.Models.Where(model => model.Available && !model.Deprecated && IsMember(family, model.Id)).ToList()
            : registry;

        if (candidates.Count == 0) candidates = registry;
        var registryOrder = registry
            .Select((model, index) => new { Id = ModelMetadataRegistry.NormalizeId(model.Id), Index = index })
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var newest = candidates
            .Select((model, index) => new { Model = model, Index = index })
            .OrderByDescending(item => Generation(item.Model.Id), VersionVectorComparer.Instance)
            .ThenBy(item => registryOrder.GetValueOrDefault(
                ModelMetadataRegistry.NormalizeId(item.Model.Id), int.MaxValue))
            .ThenBy(item => item.Index)
            .FirstOrDefault()?.Model.Id;
        if (!string.IsNullOrWhiteSpace(newest)) return newest;

        throw new InvalidOperationException($"No available model is registered for family '{family}'.");
    }

    public static string? FamilyFor(string? modelId)
        => ModelFamilies.All.FirstOrDefault(family => IsMember(family, modelId));

    private static IEnumerable<CliModelInfo> RegistryCandidates(string family)
    {
        var cliType = CliTypeFor(family);
        var vendor = cliType == CliTypes.Claude ? "anthropic" : "openai";
        return ModelMetadataRegistry.ForVendor(vendor)
            .Where(model => model.Available && !model.Deprecated && IsMember(family, model.Id))
            .Select(model => ModelMetadataRegistry.ToCliModelInfo(model, cliType));
    }

    private static bool IsFallbackSource(string? source)
        => source?.Contains("fallback", StringComparison.OrdinalIgnoreCase) == true
           || source?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;

    private static string CliTypeFor(string family)
        => family.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? CliTypes.Claude
            : CliTypes.Codex;

    private static bool IsMember(string family, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var id = ModelMetadataRegistry.NormalizeId(modelId);
        return family.ToLowerInvariant() switch
        {
            ModelFamilies.ClaudeHaiku => id.StartsWith("claude-haiku-", StringComparison.OrdinalIgnoreCase),
            ModelFamilies.ClaudeSonnet => id.StartsWith("claude-sonnet-", StringComparison.OrdinalIgnoreCase),
            ModelFamilies.ClaudeOpus => id.StartsWith("claude-opus-", StringComparison.OrdinalIgnoreCase),
            ModelFamilies.GptMini => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                                     && id.Contains("mini", StringComparison.OrdinalIgnoreCase),
            ModelFamilies.GptFlagship => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                                         && !id.Contains("mini", StringComparison.OrdinalIgnoreCase)
                                         && !id.Contains("codex", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static int[] Generation(string id)
        => VersionPart.Matches(id).Select(match => int.Parse(match.Value)).ToArray();

    private sealed class VersionVectorComparer : IComparer<int[]>
    {
        public static readonly VersionVectorComparer Instance = new();

        public int Compare(int[]? left, int[]? right)
        {
            left ??= [];
            right ??= [];
            var count = Math.Max(left.Length, right.Length);
            for (var index = 0; index < count; index++)
            {
                var comparison = (index < left.Length ? left[index] : 0)
                    .CompareTo(index < right.Length ? right[index] : 0);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
