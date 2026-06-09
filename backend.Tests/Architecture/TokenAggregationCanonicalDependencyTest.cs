using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace OrchestratorApi.Tests.Architecture;

/// <summary>
/// Keeps runtime token-usage consumers on the canonical bus-backed surface.
/// The legacy concrete services still own pure static folds for parity tests,
/// but application services should depend on <c>ITokenAggregator</c> rather
/// than injecting the old per-surface aggregators directly.
/// </summary>
public sealed class TokenAggregationCanonicalDependencyTest
{
    internal static readonly Regex LegacyServiceDependency = new(
        @"\b(?:OrchestratorApi\.Services\.(?:Runner|AdHoc)\.)?(?:TokenSummaryService|WorkspaceTokensTimelineService|ProjectTokenUsageService|AdHocUsageService)\s+\w+\b",
        RegexOptions.Compiled);

    [Fact]
    public void RuntimeServices_DoNotInjectLegacyTokenAggregators()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(violations));
    }

    [Fact]
    public void LegacyServiceDependencyRegex_MatchesInjectedConcreteTypes_AndIgnoresStaticCalls()
    {
        Assert.Matches(LegacyServiceDependency, "private readonly TokenSummaryService _tokens;");
        Assert.Matches(LegacyServiceDependency, "OrchestratorApi.Services.AdHoc.AdHocUsageService usage,");

        Assert.DoesNotMatch(LegacyServiceDependency, "TokenSummaryService.Summarize(projectName, entries);");
        Assert.DoesNotMatch(LegacyServiceDependency, "builder.Services.AddSingleton<TokenSummaryService>();");
    }

    private static List<Violation> ScanForViolations(string repoRoot)
    {
        var backendDir = Path.Combine(repoRoot, "backend");
        var violations = new List<Violation>();

        foreach (var path in Directory.EnumerateFiles(backendDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(path)) continue;

            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (IsAllowedLegacyFile(relative)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsLineCommentedOut(line)) continue;
                if (!LegacyServiceDependency.IsMatch(line)) continue;

                violations.Add(new Violation(relative, i + 1, line.Trim()));
            }
        }

        return violations;
    }

    private static bool IsAllowedLegacyFile(string relativePath)
        => relativePath.StartsWith("backend/Services/Tokens/", StringComparison.OrdinalIgnoreCase)
           || string.Equals(relativePath, "backend/Services/Runner/TokenSummary.cs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(relativePath, "backend/Services/Runner/WorkspaceTokensTimelineService.cs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(relativePath, "backend/Services/Runner/ProjectTokenUsageService.cs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(relativePath, "backend/Services/AdHoc/AdHocUsageService.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedPath(string path)
    {
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/Properties/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLineCommentedOut(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static string BuildFailureMessage(List<Violation> violations)
    {
        var rendered = string.Join("\n  ", violations.Select(v => $"{v.RelativePath}:{v.Line}: {v.Source}"));
        return
            "Found runtime dependencies on legacy token aggregation services.\n" +
            "Remediation: inject ITokenAggregator and use its bus-backed methods; keep legacy concrete services only for static pure folds, bus-reader reuse, and parity tests.\n" +
            $"Offending lines:\n  {rendered}";
    }

    private static string ResolveRepoRoot([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(thisFile), AppContext.BaseDirectory })
        {
            var current = start;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
            {
                var marker = Path.Combine(current, "backend", "OrchestratorApi.csproj");
                if (File.Exists(marker)) return current;
                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate repo root from source path '{thisFile}' or base dir '{AppContext.BaseDirectory}'.");
    }

    private readonly record struct Violation(string RelativePath, int Line, string Source);
}
