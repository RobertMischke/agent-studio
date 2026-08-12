using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Outcome of one named Quality Studio analysis step. Product findings are
/// distinct from an unavailable integration so infrastructure faults never
/// masquerade as a clean review.
/// </summary>
public enum QualityStudioAnalysisVerdict
{
    Passed,
    Findings,
    NotApplicable,
    Unavailable,
}

public sealed record QualityStudioFindingLocation(string Path, int? Line);

/// <summary>
/// The subset of Quality Studio's immutable finding envelope that Agent Studio
/// projects into task review evidence. Quality Studio remains the owner of rule
/// text, matching logic, severity, fingerprints, and stable rule ids.
/// </summary>
public sealed record QualityStudioFinding(
    string Id,
    string RuleId,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    IReadOnlyList<QualityStudioFindingLocation> Locations,
    string? Evidence);

public sealed record QualityStudioAnalysisRequest(
    string RepositoryPath,
    string Project,
    string JobId,
    string JobFolderPath,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<string> ChangedFiles)
{
    public string StepId { get; init; } = PipelineCatalogue.QualityStudioAngularRulesStepId;
    public string? RepositoryId { get; init; }
    public bool ChangedFilesKnown { get; init; } = true;
    public string? ReviewPolicyHash { get; init; }
    public int? RunIndex { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed record QualityStudioAnalysisResult(
    QualityStudioAnalysisVerdict Verdict,
    int? ExitCode,
    long DurationMs,
    string Reason,
    string Output,
    string? ArtifactRelativePath,
    IReadOnlyList<QualityStudioFinding> Findings)
{
    public bool HasActionableFindings => Findings.Any(QualityStudioAnalysisPolicy.IsActionable);
}

public interface IQualityStudioAnalysisRunner
{
    Task<QualityStudioAnalysisResult> RunAngularRulesAsync(
        QualityStudioAnalysisRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes the first Quality Studio pipeline slice through the transport seam
/// delivered by QS-74/QS-88. The executable name and portable schemas are
/// conventions, not environment-specific pipeline overrides. Project settings
/// decide whether the catalogue step runs.
/// </summary>
public sealed class QualityStudioAnalysisRunner : IQualityStudioAnalysisRunner
{
    public const string ExecutableName = "quality";
    public const string FindingSchema =
        "https://quality.studio/schemas/quality-finding.v1.schema.json";
    public const string EvidenceSchema =
        "https://quality.studio/schemas/change-review-evidence.v1.schema.json";

    private static readonly Regex FullSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<QualityStudioAnalysisRunner> _logger;

    public QualityStudioAnalysisRunner(ILogger<QualityStudioAnalysisRunner> logger)
    {
        _logger = logger;
    }

    public async Task<QualityStudioAnalysisResult> RunAngularRulesAsync(
        QualityStudioAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.StartNew();
        var selected = QualityStudioAnalysisPolicy.SelectDefaultStepIds(request.ChangedFiles);
        if (request.ChangedFilesKnown
            && !selected.Contains(request.StepId, StringComparer.Ordinal))
        {
            return Result(QualityStudioAnalysisVerdict.NotApplicable, null,
                "the task change does not touch Angular/frontend source", "", null, []);
        }

        if (!Directory.Exists(request.RepositoryPath))
        {
            return Result(QualityStudioAnalysisVerdict.Unavailable, null,
                "repository path is unavailable", "", null, []);
        }
        if (!FullSha.IsMatch(request.BaseSha) || !FullSha.IsMatch(request.HeadSha))
        {
            return Result(QualityStudioAnalysisVerdict.Unavailable, null,
                "an exact base/head Git subject is required", "", null, []);
        }

        var artifactDirectory = Path.Combine(
            request.JobFolderPath, "results", "quality-studio");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, "angular-rules.json");
        var artifactRelativePath = Path.GetRelativePath(request.JobFolderPath, artifactPath)
            .Replace('\\', '/');

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutableName,
            WorkingDirectory = Path.GetFullPath(request.RepositoryPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AddArguments(startInfo, request, artifactPath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Quality Studio Angular analysis could not start for {Project}/{JobId}",
                request.Project, request.JobId);
            return Result(QualityStudioAnalysisVerdict.Unavailable, null,
                "Quality Studio CLI could not be started", exception.Message,
                artifactRelativePath, []);
        }
        if (process is null)
        {
            return Result(QualityStudioAnalysisVerdict.Unavailable, null,
                "Quality Studio CLI did not start", "Process.Start returned null",
                artifactRelativePath, []);
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(request.Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = JoinOutput(await stdout, await stderr);
                if (process.ExitCode is not (0 or 1))
                {
                    return Result(QualityStudioAnalysisVerdict.Unavailable, process.ExitCode,
                        "Quality Studio review failed", output, artifactRelativePath, []);
                }
                if (!File.Exists(artifactPath))
                {
                    return Result(QualityStudioAnalysisVerdict.Unavailable, process.ExitCode,
                        "Quality Studio did not write its portable evidence artifact", output,
                        artifactRelativePath, []);
                }

                IReadOnlyList<QualityStudioFinding> findings;
                try
                {
                    findings = QualityStudioEvidenceParser.ParseAngularFindings(artifactPath);
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    _logger.LogWarning(exception,
                        "Quality Studio returned invalid portable evidence for {Project}/{JobId}",
                        request.Project, request.JobId);
                    return Result(QualityStudioAnalysisVerdict.Unavailable, process.ExitCode,
                        "Quality Studio evidence did not match the v1 portable contract",
                        JoinOutput(output, exception.Message), artifactRelativePath, []);
                }

                return Result(
                    findings.Count == 0
                        ? QualityStudioAnalysisVerdict.Passed
                        : QualityStudioAnalysisVerdict.Findings,
                    process.ExitCode,
                    findings.Count == 0
                        ? "Quality Studio reported no Angular named-rule findings"
                        : $"Quality Studio reported {findings.Count} Angular named-rule finding(s)",
                    output,
                    artifactRelativePath,
                    findings);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception)
                {
                    SilentCatch.Note(exception, "QualityStudioAnalysisRunner: timed-out process cleanup");
                }
                return Result(QualityStudioAnalysisVerdict.Unavailable, null,
                    $"Quality Studio analysis timed out after {request.Timeout.TotalSeconds:F0}s",
                    JoinOutput(await IgnoreCancellation(stdout), await IgnoreCancellation(stderr)),
                    artifactRelativePath, []);
            }
        }

        QualityStudioAnalysisResult Result(
            QualityStudioAnalysisVerdict verdict,
            int? exitCode,
            string reason,
            string output,
            string? relativeArtifact,
            IReadOnlyList<QualityStudioFinding> findings) =>
            new(verdict, exitCode, started.ElapsedMilliseconds, reason,
                Truncate(output), relativeArtifact, findings);
    }

    internal static void AddArguments(
        ProcessStartInfo startInfo,
        QualityStudioAnalysisRequest request,
        string artifactPath)
    {
        foreach (var argument in new[]
        {
            "diff", ".", "--base", request.BaseSha, "--head", request.HeadSha,
            "--no-write", "--fail-on-regression", "--format", "json",
            "--output", artifactPath, "--repository", request.RepositoryId ?? request.Project,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (!string.IsNullOrWhiteSpace(request.ReviewPolicyHash))
        {
            startInfo.ArgumentList.Add("--review-policy-hash");
            startInfo.ArgumentList.Add(request.ReviewPolicyHash);
        }
    }

    private static async Task<string> IgnoreCancellation(Task<string> readTask)
    {
        try { return await readTask; }
        catch { return string.Empty; }
    }

    private static string JoinOutput(params string[] values) =>
        string.Join('\n', values.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

    private static string Truncate(string value)
        => value.Length <= 12_000 ? value : value[..12_000] + "\n[truncated]";
}

/// <summary>
/// Reader for Quality Studio's transport-neutral evidence. It deliberately
/// recognizes only stable QS rule ids; unknown future findings remain in the
/// original artifact until the owning catalogue step is added here.
/// </summary>
public static class QualityStudioEvidenceParser
{
    public static IReadOnlyList<QualityStudioFinding> ParseAngularFindings(string artifactPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var root = document.RootElement;
        if (ReadString(root, "$schema") != QualityStudioAnalysisRunner.EvidenceSchema
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.GetInt32() != 1)
        {
            throw new InvalidDataException(
                "Expected Quality Studio change-review-evidence schema version 1.");
        }

        var findings = new Dictionary<string, QualityStudioFinding>(StringComparer.Ordinal);
        if (!root.TryGetProperty("reviews", out var reviews)
            || reviews.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Quality Studio evidence has no reviews array.");
        }

        foreach (var review in reviews.EnumerateArray())
        {
            if (!review.TryGetProperty("findings", out var changes)) continue;
            foreach (var bucket in new[] { "new", "persisting" })
            {
                if (!changes.TryGetProperty(bucket, out var values)
                    || values.ValueKind != JsonValueKind.Array) continue;
                foreach (var value in values.EnumerateArray())
                {
                    var parsed = ParseFinding(value);
                    if (parsed is null
                        || !QualityStudioAnalysisPolicy.AngularRuleIds.Contains(
                            parsed.RuleId, StringComparer.Ordinal)) continue;
                    findings[parsed.Fingerprint] = parsed;
                }
            }
        }

        return findings.Values
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static QualityStudioFinding? ParseFinding(JsonElement value)
    {
        if (ReadString(value, "$schema") != QualityStudioAnalysisRunner.FindingSchema
            || !value.TryGetProperty("schemaVersion", out var version)
            || version.GetInt32() != 1) return null;

        var id = ReadString(value, "id");
        var ruleId = ReadString(value, "ruleId");
        var severity = ReadString(value, "severity");
        var title = ReadString(value, "title");
        var description = ReadString(value, "description");
        var recommendation = ReadString(value, "recommendation");
        var fingerprint = ReadString(value, "fingerprint");
        if (new[] { id, ruleId, severity, title, description, recommendation, fingerprint }
            .Any(string.IsNullOrWhiteSpace)) return null;

        var locations = new List<QualityStudioFindingLocation>();
        if (value.TryGetProperty("locations", out var locationValues)
            && locationValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locationValues.EnumerateArray())
            {
                var path = ReadString(location, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                int? line = null;
                if (location.TryGetProperty("range", out var range)
                    && range.TryGetProperty("start", out var start)
                    && start.TryGetProperty("line", out var lineValue)
                    && lineValue.TryGetInt32(out var parsedLine)) line = parsedLine;
                locations.Add(new QualityStudioFindingLocation(path!, line));
            }
        }

        return new QualityStudioFinding(
            id!, ruleId!, severity!, title!, description!, recommendation!,
            fingerprint!, locations, ReadString(value, "evidence"));
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public enum QualityStudioFindingDisposition
{
    Continue,
    SteerOnce,
    Escalate,
}

public sealed record QualityStudioCardClass(bool Frontend, bool Backend)
{
    public bool Coding => Frontend || Backend;
}

/// <summary>
/// Convention-first card policy. The change set selects the standard QS axes;
/// a project's ordinary pipeline-step settings may disable or enable catalogue
/// entries later. No card field or environment variable participates.
/// </summary>
public static class QualityStudioAnalysisPolicy
{
    public static readonly string[] AngularRuleIds =
        ["QS-NG-001", "QS-NG-002", "QS-NG-003", "QS-NG-004", "QS-NG-005"];
    public static readonly string[] DotNetRuleIds =
        ["QS-CS-001", "QS-CS-002", "QS-CS-003", "QS-CS-004"];

    public static QualityStudioCardClass Classify(IEnumerable<string>? changedFiles)
    {
        var frontend = false;
        var backend = false;
        foreach (var rawPath in changedFiles ?? [])
        {
            var path = rawPath.Replace('\\', '/').TrimStart('/');
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var frontendSource = extension is ".ts" or ".html" or ".scss" or ".css" or ".js" or ".mjs";
            frontend |= frontendSource
                && (path.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("src/app/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/src/app/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains(".component.", StringComparison.OrdinalIgnoreCase));
            backend |= path.StartsWith("backend/", StringComparison.OrdinalIgnoreCase)
                || extension is ".cs" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets";
        }
        return new QualityStudioCardClass(frontend, backend);
    }

    public static IReadOnlySet<string> SelectDefaultStepIds(IEnumerable<string>? changedFiles)
    {
        var cardClass = Classify(changedFiles);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (cardClass.Frontend)
        {
            selected.Add(PipelineCatalogue.QualityStudioAngularRulesStepId);
            selected.Add(PipelineCatalogue.QualityStudioVisualStepId);
        }
        if (cardClass.Backend)
        {
            selected.Add(PipelineCatalogue.QualityStudioDotNetRulesStepId);
            selected.Add(PipelineCatalogue.QualityStudioSecurityStepId);
        }
        if (cardClass.Coding)
        {
            selected.Add(PipelineCatalogue.QualityStudioModelReviewStepId);
            selected.Add(PipelineCatalogue.QualityStudioRedundancyStepId);
            selected.Add(PipelineCatalogue.QualityStudioConsistencyStepId);
        }
        return selected;
    }

    public static bool IsActionable(QualityStudioFinding finding)
        => finding.Severity is "critical" or "high" or "medium";

    public static QualityStudioFindingDisposition Decide(
        IReadOnlyList<QualityStudioFinding> findings,
        int priorSteeredRetries)
    {
        if (!findings.Any(IsActionable)) return QualityStudioFindingDisposition.Continue;
        return priorSteeredRetries == 0
            ? QualityStudioFindingDisposition.SteerOnce
            : QualityStudioFindingDisposition.Escalate;
    }
}

/// <summary>
/// Shared projection from QS finding envelopes to Agent Studio review evidence
/// and targeted steering text. It carries QS-owned content without redefining
/// any named rule.
/// </summary>
public static class QualityStudioReviewEvidence
{
    public static void Append(
        string jobFolderPath,
        int? runIndex,
        QualityStudioAnalysisResult result)
    {
        foreach (var finding in result.Findings)
        {
            var fileRefs = finding.Locations
                .Select(location => location.Line.HasValue
                    ? $"{location.Path}:{location.Line.Value}"
                    : location.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var body = new StringBuilder(finding.Description)
                .Append("\n\nRecommendation: ")
                .Append(finding.Recommendation);
            if (!string.IsNullOrWhiteSpace(finding.Evidence))
            {
                body.Append("\n\nQuality Studio evidence: ").Append(finding.Evidence);
            }

            ReviewEvidenceLog.Append(jobFolderPath, new ReviewEvidenceEntry
            {
                Id = "quality-studio:" + finding.Fingerprint,
                Source = ReviewEvidenceSources.QualityStudio,
                Severity = finding.Severity switch
                {
                    "critical" or "high" => ReviewEvidenceSeverities.High,
                    "medium" => ReviewEvidenceSeverities.Warn,
                    _ => ReviewEvidenceSeverities.Info,
                },
                RuleId = finding.RuleId,
                Title = $"[{finding.RuleId}] {finding.Title}",
                Body = body.ToString(),
                CreatedAt = DateTime.UtcNow,
                RunIndex = runIndex,
                Artifacts = string.IsNullOrWhiteSpace(result.ArtifactRelativePath)
                    ? []
                    : [result.ArtifactRelativePath],
                FileRefs = fileRefs,
            });
        }
    }

    public static string BuildFollowUp(IReadOnlyList<QualityStudioFinding> findings) =>
        "Auto-review re-opened this task after the standard Quality Studio Angular rule pass. " +
        "Address the named findings below in the current task scope, preserve the rule ids in your close-out evidence, " +
        "and end with [[TASK_DONE]] after relevant verification. Quality Studio owns the rule definitions; do not rewrite them locally.\n\n" +
        Format(findings);

    public static string Format(IReadOnlyList<QualityStudioFinding> findings) =>
        string.Join("\n", findings.Select(finding =>
        {
            var location = finding.Locations.FirstOrDefault();
            var locationText = location is null
                ? "no location"
                : location.Line.HasValue
                    ? $"{location.Path}:{location.Line.Value}"
                    : location.Path;
            return $"- [{finding.RuleId}] {finding.Severity} at {locationText}: {finding.Title}. {finding.Recommendation}";
        }));
}
