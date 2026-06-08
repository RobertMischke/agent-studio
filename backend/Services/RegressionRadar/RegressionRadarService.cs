using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.RegressionRadar;

/// <summary>
/// Analyzes spec-file changes across a task's attributed commits and classifies
/// each change as intended, at-risk, or drift. The classification is
/// deterministic (no LLM) and uses git diff data plus companion-file
/// heuristics.
///
/// <para>Design: the service is stateless. It reads the task's attributed
/// commit chain, then delegates to <see cref="GitService"/> for file lists.
/// Classification is a pure function over the file-change list, testable
/// without git.</para>
/// </summary>
public sealed class RegressionRadarService
{
    private readonly GitService _git;
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

    public RegressionRadarResult AnalyzeProject(string projectName)
    {
        var generatedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AnalyzeProjectCore(projectName);
        sw.Stop();

        _logger.LogDebug(
            "Project regression radar for {ProjectName} generated in {DurationMs} ms ({Total} spec changes, error={Error})",
            projectName, sw.ElapsedMilliseconds, result.TotalSpecChanges, result.Error ?? "none");

        return result with { GeneratedAt = generatedAt, DurationMs = sw.ElapsedMilliseconds };
    }

    private RegressionRadarResult AnalyzeCore(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return new RegressionRadarResult { Error = "Job not found" };

        var shas = ResolveTaskCommitShas(info);
        var (baselineSha, headSha) = ResolveCommitSpan(shas);
        if (shas.Count == 0)
            return new RegressionRadarResult { BaselineSha = baselineSha, HeadSha = headSha };

        var allFiles = _git.GetAggregateCommitFiles(jobId, watchPath, shas);
        if (allFiles.Count == 0)
            return new RegressionRadarResult { BaselineSha = baselineSha, HeadSha = headSha };

        return ClassifyFiles(allFiles, baselineSha, headSha, jobId);
    }

    private RegressionRadarResult AnalyzeProjectCore(string projectName)
    {
        var jobs = _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.EnteredLaneAt)
            .ToList();

        if (jobs.Count == 0)
            return new RegressionRadarResult { Error = "Project not found" };

        var groups = new List<RegressionRadarTaskGroup>();
        var entries = new List<SpecChangeEntry>();
        string? baselineSha = null;
        string? headSha = null;

        foreach (var job in jobs)
        {
            var shas = ResolveTaskCommitShas(job);
            if (shas.Count == 0) continue;

            var (jobBaseline, jobHead) = ResolveCommitSpan(shas);
            baselineSha ??= jobBaseline;
            headSha = jobHead ?? headSha;

            var files = _git.GetAggregateCommitFiles(job.Id, job.WatchPath, shas);
            if (files.Count == 0) continue;

            var taskResult = ClassifyFiles(files, jobBaseline, jobHead, job.Id);
            if (taskResult.TotalSpecChanges == 0) continue;

            var taskEntries = taskResult.Entries
                .Select(e => e with { JobId = job.Id, JobTitle = job.Title })
                .ToList();
            entries.AddRange(taskEntries);
            groups.Add(new RegressionRadarTaskGroup
            {
                JobId = job.Id,
                JobTitle = job.Title,
                State = job.State,
                IntendedCount = taskResult.IntendedCount,
                AtRiskCount = taskResult.AtRiskCount,
                DriftCount = taskResult.DriftCount,
                TotalSpecChanges = taskResult.TotalSpecChanges,
                Entries = taskEntries,
            });
        }

        var result = BuildResult(entries, baselineSha, headSha, $"project:{projectName}");
        return result with { TaskGroups = groups };
    }

    /// <summary>
    /// Pure classification over a file-change list. Separated from
    /// <see cref="Analyze"/> so unit tests can call it without git.
    /// </summary>
    internal RegressionRadarResult ClassifyFiles(
        List<GitFileChange> allFiles, string? baselineSha, string? headSha, string jobId)
    {
        var specFiles = allFiles.Where(f => IsSpecFile(f.Path)).ToList();
        var nonSpecPaths = new HashSet<string>(
            allFiles.Where(f => !IsSpecFile(f.Path)).Select(f => f.Path),
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<SpecChangeEntry>();
        foreach (var spec in specFiles)
        {
            var (companion, companionChanged) = ResolveCompanion(spec.Path, nonSpecPaths);
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

        return BuildResult(entries, baselineSha, headSha, jobId);
    }

    private RegressionRadarResult BuildResult(
        List<SpecChangeEntry> entries, string? baselineSha, string? headSha, string jobId)
    {
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

    internal static List<string> ResolveTaskCommitShas(Models.TaskInfo info)
    {
        var shas = info.Commits
            .Select(c => c.Sha)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shas.Count == 0 && !string.IsNullOrWhiteSpace(info.Commit?.Sha))
        {
            shas.Add(info.Commit.Sha);
        }
        return shas;
    }

    private static (string? BaselineSha, string? HeadSha) ResolveCommitSpan(IReadOnlyList<string> shas)
    {
        if (shas.Count == 0) return (null, null);
        return (shas[0], shas[^1]);
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
                f.Status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
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
    /// E.g. "backend.Tests/FooTests.cs" -> "FooService.cs" (basename only; the
    /// impl lives in a parallel directory tree, so callers must match by filename)
    /// </summary>
    internal static string? ResolveCompanionPath(string specPath)
    {
        // TypeScript / JavaScript: foo.spec.ts -> foo.ts, foo.test.ts -> foo.ts
        var tsMatch = Regex.Match(specPath, @"^(.+)\.(spec|test)\.(tsx?|jsx?)$", RegexOptions.IgnoreCase);
        if (tsMatch.Success)
            return $"{tsMatch.Groups[1].Value}.{tsMatch.Groups[3].Value}";

        // .NET: FooTests.cs -> Foo.cs. Tests and implementation live in parallel
        // directory trees (backend.Tests/ vs backend/), so we only know the
        // filename, not the full path.
        var csMatch = Regex.Match(specPath, @"^(.+)Tests\.cs$", RegexOptions.IgnoreCase);
        if (csMatch.Success)
        {
            var baseName = Path.GetFileName(csMatch.Groups[1].Value);
            return baseName + ".cs";
        }

        return null;
    }

    /// <summary>
    /// Resolves a spec's companion implementation and whether it changed within
    /// the same attributed commit set. TypeScript/JS companions carry their own directory
    /// and are matched by exact relative path. .NET companions are only known by
    /// filename (test and impl live in parallel directory trees), so they are
    /// matched by basename against the changed non-spec paths; when a match is
    /// found the actual full path is returned so the reason text is precise.
    /// </summary>
    internal static (string? CompanionPath, bool CompanionChanged) ResolveCompanion(
        string specPath, IReadOnlyCollection<string> nonSpecPaths)
    {
        var companion = ResolveCompanionPath(specPath);
        if (companion == null)
            return (null, false);

        var companionHasDirectory =
            companion.IndexOf('/') >= 0 || companion.IndexOf('\\') >= 0;

        if (companionHasDirectory)
        {
            // TS/JS: exact relative-path membership.
            var changed = nonSpecPaths.Any(p =>
                string.Equals(p, companion, StringComparison.OrdinalIgnoreCase));
            return (companion, changed);
        }

        // Bare basename (.NET parallel layout): match on filename so a same-named
        // implementation in any directory counts as the companion change.
        var matched = nonSpecPaths.FirstOrDefault(p =>
            string.Equals(Path.GetFileName(p), companion, StringComparison.OrdinalIgnoreCase));
        return (matched ?? companion, matched != null);
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
        else if (name.EndsWith(".tests", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];
        else if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
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
                "Spec deleted without replacement in this task's attributed commits",
            _ => ""
        };
    }
}
