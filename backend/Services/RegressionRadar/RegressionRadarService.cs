using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.RegressionRadar;

/// <summary>
/// Analyzes spec-file changes across a task's commit range and classifies
/// each change as intended, at-risk, or drift. The classification is
/// deterministic (no LLM) and uses git diff data plus companion-file
/// heuristics.
///
/// <para>Design: the service is stateless. It reads the run timeline to
/// derive the SHA range, then delegates to <see cref="GitService"/> for
/// file lists and diffs. Classification is a pure function over the
/// file-change list, testable without git.</para>
/// </summary>
public sealed class RegressionRadarService
{
    private readonly GitService _git;
    private readonly TaskSessionLog _sessions;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<RegressionRadarService> _logger;

    private static readonly Regex SpecFilePattern = new(
        @"\.(spec|test|tests)\.(ts|tsx|js|jsx|cs|py)$|Tests\.cs$|\.test\.(ts|tsx|js|jsx)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RegressionRadarService(
        GitService git,
        TaskSessionLog sessions,
        TaskScannerService scanner,
        ILogger<RegressionRadarService> logger)
    {
        _git = git;
        _sessions = sessions;
        _scanner = scanner;
        _logger = logger;
    }

    public RegressionRadarResult Analyze(string jobId, string? watchPath)
    {
        var generatedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AnalyzeCore(jobId, watchPath);
        sw.Stop();

        _logger.LogDebug(
            "Regression radar for {JobId} generated in {DurationMs} ms ({Total} spec changes, error={Error})",
            jobId, sw.ElapsedMilliseconds, result.TotalSpecChanges, result.Error ?? "none");

        return result with { GeneratedAt = generatedAt, DurationMs = sw.ElapsedMilliseconds };
    }

    private RegressionRadarResult AnalyzeCore(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return new RegressionRadarResult { Error = "Job not found" };

        var (baselineSha, headSha) = ResolveShaRange(info, watchPath);
        if (baselineSha == null || headSha == null)
            return new RegressionRadarResult { Error = "No commit range available (task has no tracked runs with SHA data)" };

        if (string.Equals(baselineSha, headSha, StringComparison.OrdinalIgnoreCase))
            return new RegressionRadarResult { BaselineSha = baselineSha, HeadSha = headSha };

        var allFiles = _git.GetFilesChangedInShaRange(jobId, watchPath, baselineSha, headSha);
        if (allFiles.Count == 0)
            return new RegressionRadarResult { BaselineSha = baselineSha, HeadSha = headSha };

        return ClassifyFiles(allFiles, baselineSha, headSha, jobId);
    }

    /// <summary>
    /// Pure classification over a file-change list. Separated from
    /// <see cref="Analyze"/> so unit tests can call it without git.
    /// </summary>
    internal RegressionRadarResult ClassifyFiles(
        List<GitFileChange> allFiles, string baselineSha, string headSha, string jobId)
    {
        var specFiles = allFiles.Where(f => IsSpecFile(f.Path)).ToList();
        var nonSpecPaths = new HashSet<string>(
            allFiles.Where(f => !IsSpecFile(f.Path)).Select(f => f.Path),
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<SpecChangeEntry>();
        foreach (var spec in specFiles)
        {
            var companion = ResolveCompanionPath(spec.Path);
            var companionChanged = companion != null && nonSpecPaths.Contains(companion);
            var category = Classify(spec, companion, companionChanged, specFiles);
            var reason = BuildReason(spec, category, companion, companionChanged);

            entries.Add(new SpecChangeEntry
            {
                Path = spec.Path,
                FileName = Path.GetFileName(spec.Path),
                GitStatus = spec.Status,
                Category = category,
                Reason = reason,
                CompanionPath = companion,
                CompanionChanged = companionChanged,
                LinesAdded = spec.Added,
                LinesRemoved = spec.Removed,
            });
        }

        var intended = entries.Count(e => e.Category == SpecChangeCategory.Intended);
        var atRisk = entries.Count(e => e.Category == SpecChangeCategory.AtRisk);
        var drift = entries.Count(e => e.Category == SpecChangeCategory.Drift);
        var overall = drift > 0 ? SpecChangeCategory.Drift
            : atRisk > 0 ? SpecChangeCategory.AtRisk
            : SpecChangeCategory.Intended;

        _logger.LogInformation(
            "Regression radar for {JobId}: {Total} spec changes ({Intended} intended, {AtRisk} at-risk, {Drift} drift)",
            jobId, entries.Count, intended, atRisk, drift);

        return new RegressionRadarResult
        {
            OverallStatus = overall,
            IntendedCount = intended,
            AtRiskCount = atRisk,
            DriftCount = drift,
            TotalSpecChanges = entries.Count,
            BaselineSha = baselineSha,
            HeadSha = headSha,
            Entries = entries,
        };
    }

    /// <summary>
    /// Derives the full SHA range for a task by walking all runs in the
    /// timeline. Uses the first run's HeadShaBefore as baseline and the
    /// last run's HeadShaAfter as head.
    /// </summary>
    internal (string? BaselineSha, string? HeadSha) ResolveShaRange(
        Models.TaskInfo info, string? watchPath)
    {
        var events = _sessions.ReadSessionEvents(info.Id, watchPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
        if (timeline.Runs.Count == 0) return (null, null);

        string? baseline = null;
        string? head = null;

        foreach (var run in timeline.Runs)
        {
            if (baseline == null && !string.IsNullOrWhiteSpace(run.HeadShaBefore))
                baseline = run.HeadShaBefore;
            if (!string.IsNullOrWhiteSpace(run.HeadShaAfter))
                head = run.HeadShaAfter;
        }

        return (baseline, head);
    }

    /// <summary>
    /// Pure classification function. Exported as internal for unit testing.
    /// </summary>
    internal static SpecChangeCategory Classify(
        GitFileChange spec,
        string? companionPath,
        bool companionChanged,
        IReadOnlyList<GitFileChange> allSpecFiles)
    {
        var status = spec.Status.ToUpperInvariant();

        // New spec file = always intended (more coverage)
        if (status.StartsWith("A") || status.StartsWith("C"))
            return SpecChangeCategory.Intended;

        // Deleted spec: check if a replacement was added in the same range
        if (status.StartsWith("D"))
        {
            var baseName = StripSpecSuffix(Path.GetFileNameWithoutExtension(spec.Path));
            var hasReplacement = allSpecFiles.Any(f =>
                !string.Equals(f.Path, spec.Path, StringComparison.OrdinalIgnoreCase)
                && f.Status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                && StripSpecSuffix(Path.GetFileNameWithoutExtension(f.Path))
                    .Equals(baseName, StringComparison.OrdinalIgnoreCase));
            return hasReplacement ? SpecChangeCategory.Intended : SpecChangeCategory.Drift;
        }

        // Renamed spec = intended (structural refactor)
        if (status.StartsWith("R"))
            return SpecChangeCategory.Intended;

        // Modified spec: check if the companion implementation also changed
        if (status.StartsWith("M"))
        {
            if (companionChanged)
                return SpecChangeCategory.Intended;

            // No companion change - assertions were adjusted without implementation change
            return SpecChangeCategory.AtRisk;
        }

        return SpecChangeCategory.AtRisk;
    }

    internal static bool IsSpecFile(string path)
    {
        return SpecFilePattern.IsMatch(path);
    }

    /// <summary>
    /// Given a spec file path, returns the likely companion implementation path.
    /// E.g. "src/app/task.service.spec.ts" -> "src/app/task.service.ts"
    /// E.g. "backend.Tests/FooTests.cs" -> "backend/Services/Foo.cs" (heuristic)
    /// </summary>
    internal static string? ResolveCompanionPath(string specPath)
    {
        // TypeScript / JavaScript: foo.spec.ts -> foo.ts, foo.test.ts -> foo.ts
        var tsMatch = Regex.Match(specPath, @"^(.+)\.(spec|test)\.(tsx?|jsx?)$", RegexOptions.IgnoreCase);
        if (tsMatch.Success)
            return $"{tsMatch.Groups[1].Value}.{tsMatch.Groups[3].Value}";

        // .NET: FooTests.cs -> Foo.cs (same directory or parallel structure)
        var csMatch = Regex.Match(specPath, @"^(.+)Tests\.cs$", RegexOptions.IgnoreCase);
        if (csMatch.Success)
        {
            var baseName = Path.GetFileName(csMatch.Groups[1].Value);
            // The companion is in a parallel directory structure; we return
            // just the filename pattern so callers can do a contains-match
            return baseName + ".cs";
        }

        return null;
    }

    /// <summary>
    /// Strips spec/test suffixes from a filename base for replacement matching.
    /// "task.service.spec" -> "task.service"
    /// "FooTests" -> "Foo"
    /// </summary>
    internal static string StripSpecSuffix(string fileNameWithoutExtension)
    {
        var name = fileNameWithoutExtension;
        if (name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        else if (name.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        else if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        else if (name.EndsWith(".tests", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];
        return name;
    }

    private static string BuildReason(GitFileChange spec, SpecChangeCategory category, string? companion, bool companionChanged)
    {
        return category switch
        {
            SpecChangeCategory.Intended when spec.Status.StartsWith("A", StringComparison.OrdinalIgnoreCase) =>
                "New spec file (increases coverage)",
            SpecChangeCategory.Intended when spec.Status.StartsWith("R", StringComparison.OrdinalIgnoreCase) =>
                "Spec renamed (structural refactor)",
            SpecChangeCategory.Intended when companionChanged =>
                $"Spec changed alongside implementation ({companion})",
            SpecChangeCategory.Intended =>
                "Spec deleted with replacement in same range",
            SpecChangeCategory.AtRisk =>
                companion != null
                    ? $"Spec assertions changed without matching change in {companion}"
                    : "Spec assertions changed without identifiable companion implementation change",
            SpecChangeCategory.Drift =>
                "Spec deleted without replacement in the same commit range",
            _ => ""
        };
    }
}
