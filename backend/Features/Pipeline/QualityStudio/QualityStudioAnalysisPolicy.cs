namespace AgentStudio.Pipeline;

/// <summary>
/// Convention-based card classification for Quality Studio analysis steps.
/// Project settings may enable or disable catalogue steps, while rule-level
/// changes remain repository-owned in <c>.quality/rules.json</c>. No task or
/// environment override participates in this decision.
/// </summary>
public static class QualityStudioAnalysisPolicy
{
    private static readonly HashSet<string> FrontendExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".html", ".scss", ".css",
    };

    private static readonly HashSet<string> BackendExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".sln", ".slnx",
    };

    private static readonly HashSet<string> SourceExtensions = new(
        FrontendExtensions.Concat(BackendExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static QualityStudioCardPolicy Resolve(
        string repositoryPath,
        IReadOnlyCollection<string>? changedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var paths = changedFiles?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var detectedStacks = paths.Length == 0
            ? ProjectStackDetector.Detect(repositoryPath)
            : [];
        var frontend = paths.Any(IsFrontendPath)
            || detectedStacks.Contains(PipelineStepStacks.Angular, StringComparer.OrdinalIgnoreCase);
        var backend = paths.Any(IsBackendPath)
            || detectedStacks.Contains(PipelineStepStacks.DotNet, StringComparer.OrdinalIgnoreCase);
        var source = paths.Any(path => SourceExtensions.Contains(Path.GetExtension(path)))
            || paths.Length == 0 && (frontend || backend);

        var steps = new List<string>();
        if (frontend)
        {
            steps.Add(PipelineCatalogue.QualityAngularRulesStepId);
            steps.Add(PipelineCatalogue.QualityVisualStepId);
        }
        if (backend)
        {
            steps.Add(PipelineCatalogue.QualityDotNetRulesStepId);
            steps.Add(PipelineCatalogue.QualitySecurityStepId);
        }
        if (source)
        {
            steps.Add(PipelineCatalogue.QualityModelReviewStepId);
            steps.Add(PipelineCatalogue.QualityRedundancyStepId);
            steps.Add(PipelineCatalogue.QualityConsistencyStepId);
        }

        return new QualityStudioCardPolicy(
            frontend,
            backend,
            steps.Distinct(StringComparer.Ordinal).ToArray(),
            paths);
    }

    public static bool IsFrontendPath(string path) =>
        FrontendExtensions.Contains(Path.GetExtension(path));

    public static bool IsBackendPath(string path) =>
        BackendExtensions.Contains(Path.GetExtension(path));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

public sealed record QualityStudioCardPolicy(
    bool FrontendTouching,
    bool BackendTouching,
    IReadOnlyList<string> DefaultStepIds,
    IReadOnlyList<string> ChangedFiles)
{
    public bool Includes(string stepId) =>
        DefaultStepIds.Contains(stepId, StringComparer.Ordinal);
}
