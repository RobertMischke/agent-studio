namespace AgentStudio.Pipeline;

/// <summary>
/// Convention-based Quality Studio step policy. Card applicability comes from
/// the delivered change set, with repository stacks as the conservative fallback.
/// The only override is the versioned project pipeline setting. Rule-level
/// overrides belong to the analysed repository's .quality/rules.json and are
/// interpreted by Quality Studio itself.
/// </summary>
public static class QualityStudioAnalysisPolicy
{
    private static readonly HashSet<string> FrontendExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".html", ".scss", ".ts",
    };

    private static readonly HashSet<string> BackendExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".sln", ".slnx", ".targets",
    };

    public static IReadOnlyList<QualityStudioAnalysisDecision> Resolve(
        IReadOnlyCollection<string>? changedFiles,
        IReadOnlyCollection<string> repositoryStacks,
        ProjectSettings? settings)
    {
        var knownChanges = changedFiles is { Count: > 0 };
        var angularRepository = repositoryStacks.Contains(
            PipelineStepStacks.Angular, StringComparer.OrdinalIgnoreCase);
        var frontend = knownChanges
            ? changedFiles!.Any(path => IsFrontendPath(path, angularRepository))
            : angularRepository;
        var backend = knownChanges
            ? changedFiles!.Any(IsBackendPath)
            : repositoryStacks.Contains(PipelineStepStacks.DotNet, StringComparer.OrdinalIgnoreCase);
        var code = frontend || backend || (knownChanges && changedFiles!.Any(IsCodePath));

        return PipelineCatalogue.QualityStudioAnalysisSteps
            .Select(step => Decide(step, frontend, backend, code, settings))
            .ToArray();
    }

    private static QualityStudioAnalysisDecision Decide(
        PipelineStep step,
        bool frontend,
        bool backend,
        bool code,
        ProjectSettings? settings)
    {
        var conventionEnabled = step.Id switch
        {
            PipelineCatalogue.QualityStudioAngularRulesStepId => frontend,
            PipelineCatalogue.QualityStudioVisualStepId => frontend,
            PipelineCatalogue.QualityStudioDotNetRulesStepId => backend,
            PipelineCatalogue.QualityStudioSecurityStepId => backend,
            PipelineCatalogue.QualityStudioModelReviewStepId => code,
            PipelineCatalogue.QualityStudioRedundancyStepId => code,
            PipelineCatalogue.QualityStudioConsistencyStepId => code,
            _ => false,
        };
        var configured = PipelineStepConfigResolver.Lookup(settings, step.Id)?.Enabled;
        var enabled = configured ?? conventionEnabled;
        var reason = configured is not null
            ? $"project setting explicitly {(enabled ? "enabled" : "disabled")} the step"
            : enabled
                ? "enabled by card-class convention"
                : "not applicable to the delivered card class";

        // QS-90 policy: security findings remain visible and recorded, but do
        // not block delivery while the enforcement edge case is unresolved.
        var blocksOnFindings = !string.Equals(
            step.Id, PipelineCatalogue.QualityStudioSecurityStepId, StringComparison.Ordinal);
        return new QualityStudioAnalysisDecision(step, enabled, blocksOnFindings, reason);
    }

    public static bool IsFrontendPath(string path, bool angularRepository = true)
    {
        var normalized = Normalize(path);
        return FrontendExtensions.Contains(Path.GetExtension(normalized))
               && (angularRepository
                   || normalized.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBackendPath(string path)
    {
        var normalized = Normalize(path);
        return normalized.StartsWith("backend/", StringComparison.OrdinalIgnoreCase)
               || BackendExtensions.Contains(Path.GetExtension(normalized));
    }

    private static bool IsCodePath(string path)
    {
        var extension = Path.GetExtension(Normalize(path));
        return FrontendExtensions.Contains(extension) || BackendExtensions.Contains(extension)
               || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

public sealed record QualityStudioAnalysisDecision(
    PipelineStep Step,
    bool Enabled,
    bool BlocksOnFindings,
    string Reason);
