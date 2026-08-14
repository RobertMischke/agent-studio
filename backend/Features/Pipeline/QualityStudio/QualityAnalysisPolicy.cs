namespace AgentStudio.Pipeline;

/// <summary>
/// Code-owned defaults for Quality Studio analysis axes. The decision depends
/// on the immutable card change set, never on a per-card flag or environment
/// variable. Named rule enablement and severity remain owned by the repository's
/// versioned <c>.quality/rules.json</c> file and the QS rule library.
/// </summary>
public static class QualityAnalysisPolicy
{
    public const string AngularRuleAnalysis = "quality-rules";
    public const string AngularRuleAxis = "angular-rules";
    public const string DotNetRuleAxis = "dotnet-rules";
    public const string VisualAxis = "visual";
    public const string SecurityAxis = "security";

    private static readonly HashSet<string> AngularExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".html", ".scss", ".css",
    };

    private static readonly HashSet<string> DotNetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx",
    };

    public static QualityAnalysisDecision Decide(IReadOnlyList<string>? changedFiles)
    {
        var normalized = (changedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var angular = normalized
            .Where(path => AngularExtensions.Contains(Path.GetExtension(path)))
            .ToArray();
        var dotNet = normalized
            .Where(path => DotNetExtensions.Contains(Path.GetExtension(path)))
            .ToArray();

        return new QualityAnalysisDecision(
            AngularPaths: angular,
            DotNetPaths: dotNet,
            DefaultAxes: BuildAxes(angular.Length > 0, dotNet.Length > 0));
    }

    private static IReadOnlyList<string> BuildAxes(bool angular, bool dotNet)
    {
        var axes = new List<string>();
        if (angular)
        {
            axes.Add(AngularRuleAxis);
            axes.Add(VisualAxis);
        }
        if (dotNet)
        {
            axes.Add(DotNetRuleAxis);
            axes.Add(SecurityAxis);
        }
        return axes;
    }
}

public sealed record QualityAnalysisDecision(
    IReadOnlyList<string> AngularPaths,
    IReadOnlyList<string> DotNetPaths,
    IReadOnlyList<string> DefaultAxes)
{
    public bool RunsAngularRules => AngularPaths.Count > 0;
    public bool RunsDotNetRules => DotNetPaths.Count > 0;
    public bool RunsVisual => DefaultAxes.Contains(QualityAnalysisPolicy.VisualAxis, StringComparer.Ordinal);
    public bool RunsSecurity => DefaultAxes.Contains(QualityAnalysisPolicy.SecurityAxis, StringComparer.Ordinal);
}
