using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace AgentStudio.Pipeline;

/// <summary>
/// In-process adapter from an AGT analysis step to the Quality Studio package.
/// It never calls the QS HTTP API and never interprets or duplicates rule content.
/// </summary>
public sealed class QualityStudioAnalysisStepRunner
{
    public const string EvidenceSchema = "agent-studio/quality-studio-analysis-evidence/v1";
    public const string EvidenceDirectory = "quality-studio";
    public const string AngularEvidenceFile = "angular-rules.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly QualityAnalysisCore _core;
    private readonly ILogger<QualityStudioAnalysisStepRunner> _logger;
    private readonly TimeProvider _timeProvider;

    public QualityStudioAnalysisStepRunner(ILogger<QualityStudioAnalysisStepRunner> logger)
        : this(QualityAnalysisCore.CreateDefault(), logger, TimeProvider.System)
    {
    }

    public QualityStudioAnalysisStepRunner(
        QualityAnalysisCore core,
        ILogger<QualityStudioAnalysisStepRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _core = core;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<QualityStudioAnalysisRunResult> RunAngularRulesAsync(
        string repositoryPath,
        string jobFolderPath,
        IReadOnlyCollection<string>? changedFiles,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var requestedPaths = (changedFiles ?? Array.Empty<string>())
            .Where(path => QualityStudioAnalysisPolicy.IsFrontendPath(path))
            .Select(Normalize)
            .Where(path => File.Exists(Path.Combine(
                repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedPaths.Length == 0)
        {
            return await PersistAsync(
                repositoryRoot,
                jobFolderPath,
                requestedPaths,
                Array.Empty<NamedQualityAnalysisResult>(),
                QualityStudioAnalysisRunStatus.NotApplicable,
                "No existing changed Angular source file was available for analysis.",
                startedAt,
                cancellationToken);
        }

        try
        {
            var analyses = requestedPaths.Select(path => new NamedQualityAnalysis(
                RulePrecheckSensor.SensorId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reviewKind"] = "code",
                },
                QualityAnalysisScope.Path,
                path)).ToArray();
            var result = await _core.RunAsync(new QualityAnalysisRequest(
                repositoryRoot,
                analyses,
                PersistArtifacts: false), cancellationToken);
            var unavailable = result.Analyses.Where(analysis => !analysis.Available).ToArray();
            var status = unavailable.Length > 0
                ? QualityStudioAnalysisRunStatus.Unavailable
                : result.Findings.Count > 0
                    ? QualityStudioAnalysisRunStatus.Findings
                    : QualityStudioAnalysisRunStatus.Passed;
            var reason = unavailable.Length > 0
                ? string.Join("; ", unavailable.Select(analysis =>
                    $"{analysis.Name}: {analysis.UnavailableReason ?? "unavailable"}"))
                : result.Findings.Count > 0
                    ? $"Quality Studio returned {result.Findings.Count} named-rule finding(s)."
                    : "Quality Studio returned no named-rule findings.";
            return await PersistAsync(
                repositoryRoot,
                jobFolderPath,
                requestedPaths,
                result.Analyses,
                status,
                reason,
                startedAt,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Quality Studio Angular analysis failed for repository {RepositoryPath}", repositoryRoot);
            return await PersistAsync(
                repositoryRoot,
                jobFolderPath,
                requestedPaths,
                Array.Empty<NamedQualityAnalysisResult>(),
                QualityStudioAnalysisRunStatus.Failed,
                ex.Message,
                startedAt,
                cancellationToken);
        }
    }

    private async Task<QualityStudioAnalysisRunResult> PersistAsync(
        string repositoryPath,
        string jobFolderPath,
        IReadOnlyList<string> requestedPaths,
        IReadOnlyList<NamedQualityAnalysisResult> analyses,
        QualityStudioAnalysisRunStatus status,
        string reason,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var findings = analyses.SelectMany(analysis => analysis.Findings)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
            .ToArray();
        var relativeArtifact = $"results/{EvidenceDirectory}/{AngularEvidenceFile}";
        var result = new QualityStudioAnalysisRunResult(
            PipelineCatalogue.QualityStudioAngularRulesStepId,
            status,
            reason,
            findings,
            relativeArtifact,
            startedAt,
            completedAt);
        var evidence = new QualityStudioAnalysisEvidence(
            EvidenceSchema,
            result.StepId,
            RulePrecheckSensor.SensorId,
            PackageVersion(),
            repositoryPath,
            ".quality/rules.json",
            requestedPaths,
            analyses,
            StatusToken(status),
            reason,
            startedAt,
            completedAt);

        var evidenceDirectory = Path.Combine(jobFolderPath, "results", EvidenceDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        var evidencePath = Path.Combine(evidenceDirectory, AngularEvidenceFile);
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(evidence, JsonOptions) + Environment.NewLine,
            cancellationToken);

        foreach (var finding in findings)
        {
            ReviewEvidenceLog.Append(jobFolderPath, new ReviewEvidenceEntry
            {
                Id = "qs-" + finding.Id,
                Source = ReviewEvidenceSources.CodeReview,
                Severity = ToEvidenceSeverity(finding.Severity),
                Title = $"{finding.RuleId}: {finding.Title}",
                Body = $"{finding.Description}\n\nRecommendation: {finding.Recommendation}",
                CreatedAt = completedAt,
                Artifacts = [relativeArtifact],
                FileRefs = finding.Locations.Select(LocationReference).ToList(),
            });
        }

        return result;
    }

    private static string LocationReference(FindingLocation location) =>
        location.Range is null
            ? location.Path
            : $"{location.Path}:{location.Range.Start.Line}";

    private static string ToEvidenceSeverity(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical or FindingSeverity.High => ReviewEvidenceSeverities.High,
        FindingSeverity.Medium => ReviewEvidenceSeverities.Warn,
        _ => ReviewEvidenceSeverities.Info,
    };

    private static string PackageVersion() =>
        typeof(QualityAnalysisCore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(QualityAnalysisCore).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string StatusToken(QualityStudioAnalysisRunStatus status) => status switch
    {
        QualityStudioAnalysisRunStatus.Passed => "passed",
        QualityStudioAnalysisRunStatus.Findings => "findings",
        QualityStudioAnalysisRunStatus.NotApplicable => "not-applicable",
        QualityStudioAnalysisRunStatus.Unavailable => "unavailable",
        _ => "failed",
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public enum QualityStudioAnalysisRunStatus
{
    Passed,
    Findings,
    NotApplicable,
    Unavailable,
    Failed,
}

public sealed record QualityStudioAnalysisRunResult(
    string StepId,
    QualityStudioAnalysisRunStatus Status,
    string Reason,
    IReadOnlyList<ReviewFinding> Findings,
    string Artifact,
    DateTime StartedAt,
    DateTime CompletedAt)
{
    public long DurationMs => Math.Max(0L, (long)(CompletedAt - StartedAt).TotalMilliseconds);

    public AspectVerdict ToBlockingVerdict()
    {
        var lines = Findings.Take(12).Select(finding =>
        {
            var location = finding.Locations.FirstOrDefault();
            var at = location is null ? "" : $" at {LocationReference(location)}";
            return $"- {finding.RuleId}: {finding.Title}{at}. {finding.Recommendation}";
        });
        var suffix = Findings.Count > 12 ? $"\n- {Findings.Count - 12} more finding(s) in {Artifact}." : "";
        return new AspectVerdict(
            "quality-studio-angular-rules",
            AspectStatus.Block,
            $"Quality Studio found {Findings.Count} Angular rule violation(s); see {Artifact}.",
            string.Join("\n", lines) + suffix,
            "quality:concerns");
    }

    private static string LocationReference(FindingLocation location) =>
        location.Range is null ? location.Path : $"{location.Path}:{location.Range.Start.Line}";
}

public sealed record QualityStudioAnalysisEvidence(
    string Schema,
    string StepId,
    string Analysis,
    string PackageVersion,
    string RepositoryPath,
    string RepositoryRuleConfig,
    IReadOnlyList<string> RequestedPaths,
    IReadOnlyList<NamedQualityAnalysisResult> Analyses,
    string Status,
    string Reason,
    DateTime StartedAt,
    DateTime CompletedAt);
