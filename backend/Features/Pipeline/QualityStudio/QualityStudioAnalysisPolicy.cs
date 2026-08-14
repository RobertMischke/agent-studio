using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Convention-first selection of Quality Studio analysis axes. The only
/// override is repository-owned JSON, so the same commit receives the same
/// policy on every host.
/// </summary>
public static class QualityStudioAnalysisPolicy
{
    public const string ConfigurationPath = ".quality/agent-studio-pipeline.json";
    public const string ConfigurationSchema =
        "https://agent-taskboard.local/schemas/quality-analysis-policy.v1.schema.json";

    private static readonly HashSet<string> FrontendTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "frontend", "front-end", "ui", "ux", "angular", "area-frontend",
    };

    private static readonly HashSet<string> BackendTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "backend", "api", "dotnet", "csharp", "c#", "area-backend",
    };

    public static QualityStudioAnalysisSelection Resolve(
        QualityStudioCardFacts facts,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var changedFiles = facts.ChangedFiles ?? [];
        var frontend = changedFiles.Any(path => IsFrontendSource(path, facts.RepositoryPath))
            || HasTag(facts.Tags, FrontendTags)
            || ContainsHint(facts.Title, "frontend", "front-end", "angular");
        var backend = changedFiles.Any(IsBackendSource)
            || HasTag(facts.Tags, BackendTags)
            || ContainsHint(facts.Title, "backend", ".net", "c#");
        var coding = frontend || backend;

        var defaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [PipelineCatalogue.QualityStudioRuleAnalysisStepId] = coding,
            [PipelineCatalogue.QualityStudioModelReviewStepId] = coding,
            [PipelineCatalogue.QualityStudioVisualQualityStepId] = frontend,
            [PipelineCatalogue.QualityStudioSecurityStepId] = backend,
            [PipelineCatalogue.QualityStudioRedundancyStepId] = coding,
            [PipelineCatalogue.QualityStudioConsistencyStepId] = coding,
        };

        if (overrides is not null)
        {
            foreach (var (stepId, enabled) in overrides) defaults[stepId] = enabled;
        }

        return new QualityStudioAnalysisSelection(
            FrontendTouching: frontend,
            BackendTouching: backend,
            EnabledStepIds: PipelineCatalogue.QualityStudioAnalysisStepIds
                .Where(stepId => defaults.GetValueOrDefault(stepId))
                .ToArray());
    }

    public static IReadOnlyDictionary<string, bool> LoadOverrides(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var path = Path.Combine(
            Path.GetFullPath(repositoryPath),
            ConfigurationPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(property =>
                property.Name is not ("$schema" or "schemaVersion" or "steps"))
            || !root.TryGetProperty("$schema", out var schema)
            || schema.ValueKind != JsonValueKind.String
            || !string.Equals(schema.GetString(), ConfigurationSchema, StringComparison.Ordinal)
            || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !root.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{ConfigurationPath} must be a v1 Quality Studio pipeline policy object.");
        }

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps.EnumerateObject())
        {
            if (!PipelineCatalogue.IsQualityStudioAnalysisStep(step.Name))
                throw new InvalidDataException(
                    $"{ConfigurationPath} references unknown analysis step '{step.Name}'.");
            if (step.Value.ValueKind != JsonValueKind.Object
                || step.Value.EnumerateObject().Any(property => property.Name != "enabled")
                || !step.Value.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    $"{ConfigurationPath} override '{step.Name}' must contain one boolean 'enabled' property.");
            }
            result[step.Name] = enabled.GetBoolean();
        }
        return result;
    }

    public static bool IsRuleSource(string? path) =>
        IsSupportedFrontendExtension(path) || IsBackendSource(path);

    public static string? NormalizeRuleSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return null;
        var normalized = path!.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment => segment is not ("." or ".."))
            && IsRuleSource(normalized)
                ? string.Join('/', segments)
                : null;
    }

    /// <summary>
    /// Security findings stay recorded and visible but do not block the
    /// pipeline. All other analysis axes use the normal severity floor.
    /// </summary>
    public static bool BlocksPipeline(string stepId, string severity) =>
        !string.Equals(
            stepId,
            PipelineCatalogue.QualityStudioSecurityStepId,
            StringComparison.OrdinalIgnoreCase)
        && severity.ToLowerInvariant() is "critical" or "high" or "medium";

    private static bool IsFrontendSource(string? path, string? repositoryPath)
    {
        if (!IsSupportedFrontendExtension(path)) return false;
        var normalized = path!.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(repositoryPath)) return false;

        var root = Path.GetFullPath(repositoryPath);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        for (var directory = Path.GetDirectoryName(fullPath);
             directory is not null
             && (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
                 || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
             directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "angular.json"))) return true;
            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static bool IsSupportedFrontendExtension(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".html" or ".scss" or ".css";

    private static bool IsBackendSource(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    private static bool HasTag(IEnumerable<string>? tags, IReadOnlySet<string> hints) =>
        tags?.Any(tag => !string.IsNullOrWhiteSpace(tag) && hints.Contains(tag.Trim())) == true;

    private static bool ContainsHint(string? value, params string[] hints) =>
        !string.IsNullOrWhiteSpace(value)
        && hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
}

public sealed record QualityStudioCardFacts(
    string? TaskType,
    IReadOnlyCollection<string>? Tags,
    string? Title,
    IReadOnlyCollection<string>? ChangedFiles,
    string? RepositoryPath = null);

public sealed record QualityStudioAnalysisSelection(
    bool FrontendTouching,
    bool BackendTouching,
    IReadOnlyList<string> EnabledStepIds)
{
    public bool Runs(string stepId) =>
        EnabledStepIds.Contains(stepId, StringComparer.OrdinalIgnoreCase);
}
