using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public enum QualityStudioAnalysisVerdict
{
    Pass,
    Findings,
    NotApplicable,
    Unavailable,
}

public sealed record QualityStudioAnalysisStepRequest(
    string RepositoryPath,
    string JobFolderPath,
    IReadOnlyCollection<string>? ChangedFiles,
    int? RunIndex = null);

public sealed record QualityStudioAnalysisStepResult(
    QualityStudioAnalysisVerdict Verdict,
    IReadOnlyList<ReviewFinding> Findings,
    long DurationMs,
    string Reason,
    string? ArtifactPath);

public interface IQualityStudioAnalysisStepRunner
{
    Task<QualityStudioAnalysisStepResult> RunAngularRulesAsync(
        QualityStudioAnalysisStepRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// First Quality Studio pipeline executor. It calls the QS analysis core in the
/// backend process, keeps repository writes disabled, and translates the QS
/// finding model into Agent Studio review evidence.
/// </summary>
public sealed class QualityStudioAnalysisStepRunner : IQualityStudioAnalysisStepRunner
{
    public const string Provider = "quality-studio";
    public const string AngularRuleAnalysis = "quality-rules";
    public const string ArtifactRelativePath = "results/quality-studio/angular-rules.json";

    private static readonly JsonSerializerOptions EvidenceJson = CreateEvidenceJson();
    private readonly QualityAnalysisCore core;
    private readonly ILogger<QualityStudioAnalysisStepRunner> logger;

    public QualityStudioAnalysisStepRunner(ILogger<QualityStudioAnalysisStepRunner> logger)
        : this(QualityAnalysisCore.CreateDefault(), logger)
    {
    }

    internal QualityStudioAnalysisStepRunner(
        QualityAnalysisCore core,
        ILogger<QualityStudioAnalysisStepRunner> logger)
    {
        this.core = core;
        this.logger = logger;
    }

    public async Task<QualityStudioAnalysisStepResult> RunAngularRulesAsync(
        QualityStudioAnalysisStepRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var policy = QualityStudioAnalysisPolicy.Resolve(request.RepositoryPath, request.ChangedFiles);
        if (!policy.Includes(PipelineCatalogue.QualityAngularRulesStepId))
        {
            return new QualityStudioAnalysisStepResult(
                QualityStudioAnalysisVerdict.NotApplicable, [], 0,
                "The card does not touch Angular source.", null);
        }

        var descriptors = core.ListAnalyses();
        if (!descriptors.Any(item => string.Equals(item.Name, AngularRuleAnalysis, StringComparison.Ordinal)))
        {
            return await PersistAsync(
                request,
                startedAt,
                stopwatch,
                QualityStudioAnalysisVerdict.Unavailable,
                [],
                $"The in-process Quality Studio package does not expose '{AngularRuleAnalysis}'.",
                [],
                cancellationToken);
        }

        try
        {
            var selectedPaths = policy.ChangedFiles
                .Where(QualityStudioAnalysisPolicy.IsFrontendPath)
                .Where(path => File.Exists(Path.Combine(
                    request.RepositoryPath,
                    path.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();
            IReadOnlyList<NamedQualityAnalysis> analyses = selectedPaths.Length == 0
                ? [RulesAnalysis()]
                : selectedPaths.Select(path => RulesAnalysis(path)).ToArray();
            var result = await core.RunAsync(
                new QualityAnalysisRequest(request.RepositoryPath, analyses, PersistArtifacts: false),
                cancellationToken).ConfigureAwait(false);
            var unavailable = result.Analyses.FirstOrDefault(item => !item.Available);
            if (unavailable is not null)
            {
                return await PersistAsync(
                    request,
                    startedAt,
                    stopwatch,
                    QualityStudioAnalysisVerdict.Unavailable,
                    [],
                    unavailable.UnavailableReason ?? $"Quality Studio analysis '{unavailable.Name}' is unavailable.",
                    result.Analyses,
                    cancellationToken);
            }

            var findings = result.Findings
                .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray();
            var verdict = findings.Length == 0
                ? QualityStudioAnalysisVerdict.Pass
                : QualityStudioAnalysisVerdict.Findings;
            var reason = findings.Length == 0
                ? "Quality Studio Angular rules passed."
                : $"Quality Studio Angular rules reported {findings.Length} finding(s).";
            return await PersistAsync(
                request, startedAt, stopwatch, verdict, findings, reason,
                result.Analyses, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Quality Studio Angular rule analysis failed for {RepositoryPath}",
                request.RepositoryPath);
            return await PersistAsync(
                request,
                startedAt,
                stopwatch,
                QualityStudioAnalysisVerdict.Unavailable,
                [],
                exception.Message,
                [],
                cancellationToken);
        }
    }

    private static NamedQualityAnalysis RulesAnalysis(string? path = null) => new(
        AngularRuleAnalysis,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reviewKind"] = "code",
        },
        path is null ? QualityAnalysisScope.Repository : QualityAnalysisScope.Path,
        path);

    private async Task<QualityStudioAnalysisStepResult> PersistAsync(
        QualityStudioAnalysisStepRequest request,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        QualityStudioAnalysisVerdict verdict,
        IReadOnlyList<ReviewFinding> findings,
        string reason,
        IReadOnlyList<NamedQualityAnalysisResult> analyses,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        var completedAt = DateTimeOffset.UtcNow;
        var artifactPath = Path.Combine(
            request.JobFolderPath,
            ArtifactRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var evidence = new QualityStudioAnalysisEvidence(
            SchemaVersion: 1,
            StepId: PipelineCatalogue.QualityAngularRulesStepId,
            Provider,
            AnalysisName: AngularRuleAnalysis,
            PackageVersion: PackageVersion(),
            RuleConfiguration: File.Exists(Path.Combine(request.RepositoryPath, ".quality", "rules.json"))
                ? ".quality/rules.json"
                : null,
            PersistedRepositoryArtifacts: false,
            Verdict: verdict.ToString().ToLowerInvariant(),
            Reason: reason,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Analyses: analyses,
            Findings: findings);
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(evidence, EvidenceJson) + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        foreach (var finding in findings)
        {
            ReviewEvidenceLog.Append(request.JobFolderPath, ToReviewEvidence(finding, request.RunIndex));
        }

        return new QualityStudioAnalysisStepResult(
            verdict,
            findings,
            stopwatch.ElapsedMilliseconds,
            reason,
            ArtifactRelativePath);
    }

    private static ReviewEvidenceEntry ToReviewEvidence(ReviewFinding finding, int? runIndex) => new()
    {
        Id = $"quality-studio:{finding.Fingerprint}",
        RuleId = finding.RuleId,
        Source = ReviewEvidenceSources.QualityStudio,
        Severity = finding.Severity switch
        {
            FindingSeverity.Critical or FindingSeverity.High => ReviewEvidenceSeverities.High,
            FindingSeverity.Medium => ReviewEvidenceSeverities.Warn,
            _ => ReviewEvidenceSeverities.Info,
        },
        Title = $"{finding.RuleId}: {finding.Title}",
        Body = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { finding.Description, finding.Recommendation, finding.Evidence }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
        CreatedAt = DateTime.UtcNow,
        RunIndex = runIndex,
        Artifacts = [ArtifactRelativePath],
        FileRefs = finding.Locations.Select(location =>
            location.Range is null
                ? location.Path
                : $"{location.Path}:{location.Range.Start.Line}")
            .Distinct(StringComparer.Ordinal)
            .ToList(),
    };

    private static string PackageVersion()
    {
        var assembly = typeof(QualityAnalysisCore).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static JsonSerializerOptions CreateEvidenceJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record QualityStudioAnalysisEvidence(
    int SchemaVersion,
    string StepId,
    string Provider,
    string AnalysisName,
    string PackageVersion,
    string? RuleConfiguration,
    bool PersistedRepositoryArtifacts,
    string Verdict,
    string Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMs,
    IReadOnlyList<NamedQualityAnalysisResult> Analyses,
    IReadOnlyList<ReviewFinding> Findings);
