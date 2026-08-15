using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

public enum QualityStudioCardClass
{
    Other,
    Frontend,
    Backend,
    Mixed,
}

/// <summary>
/// Versioned, repository-owned overrides for Quality Studio pipeline steps.
/// This file is the only override source. Environment variables and central
/// project settings deliberately do not participate in resolution.
/// </summary>
public sealed record QualityStudioProjectPolicy
{
    public const int CurrentSchemaVersion = 1;
    public const string RelativePath = ".quality/agent-studio.json";
    public const string RuleConfigurationRelativePath = ".quality/rules.json";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Dictionary<string, bool> AnalysisSteps { get; init; } = new(StringComparer.Ordinal);
}

public sealed record QualityStudioAnalysisSelection(
    QualityStudioCardClass CardClass,
    IReadOnlyList<string> StepIds,
    IReadOnlyList<string> RuleProfiles,
    string? OverridePath,
    string RuleConfigurationPath)
{
    public bool Runs(string stepId) => StepIds.Contains(stepId, StringComparer.Ordinal);
}

/// <summary>
/// Pure convention-over-configuration policy for the Quality Studio analysis
/// bracket. Defaults derive from changed paths; a project can override named
/// steps only through the versioned file in its own repository.
/// </summary>
public static class QualityStudioAnalysisPolicy
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> KnownSteps =
        PipelineCatalogue.QualityAnalysisStepIds.ToHashSet(StringComparer.Ordinal);

    public static QualityStudioAnalysisSelection Resolve(
        string repositoryPath,
        IEnumerable<string> changedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedPaths);

        var root = Path.GetFullPath(repositoryPath);
        var paths = changedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cardClass = Classify(paths);
        var enabled = Defaults(cardClass);
        var overridePath = Path.Combine(root, QualityStudioProjectPolicy.RelativePath);
        string? effectiveOverridePath = null;
        if (File.Exists(overridePath))
        {
            var policy = JsonSerializer.Deserialize<QualityStudioProjectPolicy>(
                File.ReadAllText(overridePath), ReadOptions)
                ?? throw new InvalidDataException($"Quality Studio policy is empty: {overridePath}");
            Validate(policy, overridePath);
            foreach (var (stepId, isEnabled) in policy.AnalysisSteps)
            {
                if (isEnabled) enabled.Add(stepId);
                else enabled.Remove(stepId);
            }
            effectiveOverridePath = QualityStudioProjectPolicy.RelativePath;
        }

        var ordered = PipelineCatalogue.QualityAnalysisStepIds.Where(enabled.Contains).ToArray();
        return new QualityStudioAnalysisSelection(
            cardClass,
            ordered,
            RuleProfiles(cardClass),
            effectiveOverridePath,
            QualityStudioProjectPolicy.RuleConfigurationRelativePath);
    }

    public static QualityStudioCardClass Classify(IEnumerable<string> changedPaths)
    {
        var frontend = false;
        var backend = false;
        foreach (var rawPath in changedPaths)
        {
            var path = NormalizePath(rawPath);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            frontend |= path.Equals("angular.json", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)
                || extension is ".ts" or ".html" or ".scss" or ".css";
            backend |= path.StartsWith("backend/", StringComparison.OrdinalIgnoreCase)
                || extension is ".cs" or ".csproj" or ".sln" or ".slnx";
        }

        return (frontend, backend) switch
        {
            (true, true) => QualityStudioCardClass.Mixed,
            (true, false) => QualityStudioCardClass.Frontend,
            (false, true) => QualityStudioCardClass.Backend,
            _ => QualityStudioCardClass.Other,
        };
    }

    /// <summary>
    /// Security findings stay recorded and visible but are non-blocking until
    /// the policy is deliberately revised. Other findings may steer a retry.
    /// </summary>
    public static bool FindingsBlock(string stepId) =>
        !string.Equals(stepId, PipelineCatalogue.QualitySecurityStepId, StringComparison.Ordinal);

    private static HashSet<string> Defaults(QualityStudioCardClass cardClass)
    {
        var result = new HashSet<string>(StringComparer.Ordinal)
        {
            PipelineCatalogue.QualityStaticRulesStepId,
        };
        if (cardClass is QualityStudioCardClass.Frontend or QualityStudioCardClass.Mixed)
            result.Add(PipelineCatalogue.QualityVisualStepId);
        if (cardClass is QualityStudioCardClass.Backend or QualityStudioCardClass.Mixed)
            result.Add(PipelineCatalogue.QualitySecurityStepId);
        return result;
    }

    private static IReadOnlyList<string> RuleProfiles(QualityStudioCardClass cardClass) => cardClass switch
    {
        QualityStudioCardClass.Frontend => ["angular"],
        QualityStudioCardClass.Backend => ["dotnet"],
        QualityStudioCardClass.Mixed => ["angular", "dotnet"],
        _ => ["core"],
    };

    private static void Validate(QualityStudioProjectPolicy policy, string path)
    {
        if (policy.SchemaVersion != QualityStudioProjectPolicy.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported Quality Studio policy schemaVersion '{policy.SchemaVersion}' in {path}.");
        var unknown = policy.AnalysisSteps.Keys.Where(step => !KnownSteps.Contains(step)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidDataException(
                $"Unknown Quality Studio analysis step(s) in {path}: {string.Join(", ", unknown)}.");
    }

    private static string NormalizePath(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('/');
}
