using System.Diagnostics;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using AgentStudio.Persistence;

namespace AgentStudio.Pipeline;

public interface IQualityStudioAnalysisCore
{
    Task<QualityStudioCoreResult> RunRuleAnalysisAsync(
        string repositoryPath,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thin host adapter over the Quality Studio DLL. Rule implementation, rule
/// content, matching, finding identity, severity, and provenance stay owned by
/// the package.
/// </summary>
public sealed class QualityStudioPackageAnalysisCore : IQualityStudioAnalysisCore
{
    public const string PackageId = "AgentOrchestrator.CodeQuality";
    public const string PackageVersion = "0.1.0-agt2655.1";

    public async Task<QualityStudioCoreResult> RunRuleAnalysisAsync(
        string repositoryPath,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(repositoryPath);
        var normalizedPaths = paths
            .Select(path => QualityStudioAnalysisPolicy.NormalizeRuleSourcePath(path)
                ?? throw new InvalidDataException($"Quality analysis path '{path}' is not repository-relative source."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var resolvedRules = new RuleLibrary().Resolve(fullRoot, "code", normalizedPaths);
        var core = new QualityAnalysisCore([new RulePrecheckSensor()]);
        var findings = new List<QualityStudioFinding>();
        var provenance = new List<QualityStudioProvenance>();

        foreach (var path in normalizedPaths)
        {
            var result = await core.RunAsync(new QualityAnalysisRequest(
                fullRoot,
                [new NamedQualityAnalysis(
                    RulePrecheckSensor.SensorId,
                    new Dictionary<string, string> { ["reviewKind"] = "code" },
                    QualityAnalysisScope.Path,
                    path)],
                PersistArtifacts: false), cancellationToken).ConfigureAwait(false);
            var analysis = result.Analyses.Single();
            provenance.Add(new QualityStudioProvenance(
                analysis.Provenance.SensorId,
                analysis.Provenance.SensorVersion,
                analysis.Provenance.Scope,
                analysis.Provenance.Target,
                analysis.Provenance.ScannedAt));
            if (!analysis.Available)
            {
                return new QualityStudioCoreResult(
                    false,
                    analysis.UnavailableReason ?? "Quality Studio rule analysis is unavailable.",
                    resolvedRules.Rules.Select(rule => rule.Definition.Id).ToArray(),
                    [],
                    provenance);
            }

            findings.AddRange(analysis.Findings.Select(Map));
        }

        return new QualityStudioCoreResult(
            true,
            null,
            resolvedRules.Rules.Select(rule => rule.Definition.Id).ToArray(),
            findings.DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray(),
            provenance);
    }

    private static QualityStudioFinding Map(ReviewFinding finding)
    {
        var location = finding.Locations.FirstOrDefault();
        return new QualityStudioFinding(
            finding.Id,
            finding.RuleId,
            finding.Aspect,
            finding.Severity.ToString().ToLowerInvariant(),
            finding.Title,
            finding.Description,
            finding.Recommendation,
            finding.Fingerprint,
            location?.Path,
            location?.Range?.Start.Line,
            location?.Range?.Start.Column,
            finding.Source?.SensorId,
            finding.Source?.ProducerVersion,
            finding.Evidence);
    }
}

public sealed class QualityStudioRuleAnalysisRunner
{
    public const string ArtifactRelativePath =
        "results/quality-studio/post-qs-rule-analysis.v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IQualityStudioAnalysisCore core;
    private readonly IAtomicJsonFileWriter writer;

    public QualityStudioRuleAnalysisRunner(
        IQualityStudioAnalysisCore core,
        IAtomicJsonFileWriter writer)
    {
        this.core = core;
        this.writer = writer;
    }

    public async Task<QualityStudioRuleAnalysisOutcome> RunAsync(
        QualityStudioRuleAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        QualityStudioAnalysisSelection? selection = null;
        try
        {
            var overrides = QualityStudioAnalysisPolicy.LoadOverrides(request.RepositoryPath);
            selection = QualityStudioAnalysisPolicy.Resolve(
                new QualityStudioCardFacts(
                    request.TaskType,
                    request.Tags,
                    request.Title,
                    request.ChangedFiles,
                    request.RepositoryPath),
                overrides);
            if (!selection.Runs(PipelineCatalogue.QualityStudioRuleAnalysisStepId))
            {
                return Persist(request, selection, stopwatch.ElapsedMilliseconds,
                    QualityStudioRuleAnalysisVerdict.NotApplicable,
                    "The card does not touch an Angular or C# source class selected by policy.",
                    [], [], []);
            }

            var paths = (request.ChangedFiles ?? [])
                .Select(QualityStudioAnalysisPolicy.NormalizeRuleSourcePath)
                .OfType<string>()
                .Where(path => File.Exists(Path.Combine(
                    request.RepositoryPath,
                    path.Replace('/', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                return Persist(request, selection, stopwatch.ElapsedMilliseconds,
                    QualityStudioRuleAnalysisVerdict.NotApplicable,
                    "No changed Angular or C# source file is available in the reviewed repository snapshot.",
                    [], [], []);
            }

            var result = await core.RunRuleAnalysisAsync(
                request.RepositoryPath, paths, cancellationToken).ConfigureAwait(false);
            if (!result.Available)
            {
                return Persist(request, selection, stopwatch.ElapsedMilliseconds,
                    QualityStudioRuleAnalysisVerdict.Failed,
                    result.UnavailableReason ?? "Quality Studio rule analysis is unavailable.",
                    paths, result.AppliedRuleIds, [], result.Provenance);
            }

            var blocking = result.Findings
                .Where(finding => QualityStudioAnalysisPolicy.BlocksPipeline(
                    PipelineCatalogue.QualityStudioRuleAnalysisStepId,
                    finding.Severity))
                .ToArray();
            var verdict = blocking.Length > 0
                ? QualityStudioRuleAnalysisVerdict.Findings
                : QualityStudioRuleAnalysisVerdict.Passed;
            var reason = blocking.Length > 0
                ? $"Quality Studio found {blocking.Length} blocking named-rule violation(s)."
                : $"Quality Studio checked {paths.Length} changed source file(s) with no blocking named-rule violation.";
            var outcome = Persist(request, selection, stopwatch.ElapsedMilliseconds,
                verdict, reason, paths, result.AppliedRuleIds, result.Findings, result.Provenance);
            AppendReviewEvidence(request, result.Findings);
            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Persist(request, selection, stopwatch.ElapsedMilliseconds,
                QualityStudioRuleAnalysisVerdict.Failed,
                exception.Message,
                request.ChangedFiles?
                    .Select(QualityStudioAnalysisPolicy.NormalizeRuleSourcePath)
                    .OfType<string>()
                    .ToArray() ?? [],
                [], []);
        }
    }

    private QualityStudioRuleAnalysisOutcome Persist(
        QualityStudioRuleAnalysisRequest request,
        QualityStudioAnalysisSelection? selection,
        long durationMs,
        QualityStudioRuleAnalysisVerdict verdict,
        string reason,
        IReadOnlyList<string> analyzedPaths,
        IReadOnlyList<string> appliedRuleIds,
        IReadOnlyList<QualityStudioFinding> findings,
        IReadOnlyList<QualityStudioProvenance>? provenance = null)
    {
        var evidence = new QualityStudioAnalysisEvidence(
            Schema: "https://agent-taskboard.local/schemas/quality-studio-analysis-evidence.v1.schema.json",
            SchemaVersion: 1,
            StepId: PipelineCatalogue.QualityStudioRuleAnalysisStepId,
            PackageId: QualityStudioPackageAnalysisCore.PackageId,
            PackageVersion: QualityStudioPackageAnalysisCore.PackageVersion,
            QsSourceCommits:
            [
                "QS-90:9e9461f1025afa18c6082a3f82f5649bfdcdf4e3",
                "QS-91:1be25369af83c9aff6548c0382bce9b63c6dd9a0",
            ],
            ConfigurationPath: QualityStudioAnalysisPolicy.ConfigurationPath,
            RuleConfigurationPath: ".quality/rules.json",
            FrontendTouching: selection?.FrontendTouching ?? false,
            BackendTouching: selection?.BackendTouching ?? false,
            EnabledSteps: selection?.EnabledStepIds ?? [],
            Verdict: VerdictToken(verdict),
            Reason: reason,
            DurationMs: durationMs,
            AnalyzedPaths: analyzedPaths,
            AppliedRuleIds: appliedRuleIds,
            Findings: findings,
            Provenance: provenance ?? []);
        var path = Path.Combine(
            request.JobFolderPath,
            ArtifactRelativePath.Replace('/', Path.DirectorySeparatorChar));
        writer.Write(path, JsonSerializer.Serialize(evidence, JsonOptions));
        return new QualityStudioRuleAnalysisOutcome(
            verdict, reason, durationMs, findings, ArtifactRelativePath);
    }

    private static void AppendReviewEvidence(
        QualityStudioRuleAnalysisRequest request,
        IReadOnlyList<QualityStudioFinding> findings)
    {
        foreach (var finding in findings)
        {
            var location = finding.Path is null
                ? null
                : finding.Line is null ? finding.Path : $"{finding.Path}:{finding.Line}";
            ReviewEvidenceLog.Append(request.JobFolderPath, new ReviewEvidenceEntry
            {
                Id = $"qs:{finding.Fingerprint}",
                Source = ReviewEvidenceSources.CodeReview,
                Severity = EvidenceSeverity(finding.Severity),
                Title = $"[{finding.RuleId}] {finding.Title}",
                Body = $"Rule `{finding.RuleId}`: {finding.Description}\n\nRecommendation: {finding.Recommendation}",
                CreatedAt = DateTime.UtcNow,
                RunIndex = request.RunIndex,
                Artifacts = [ArtifactRelativePath],
                FileRefs = location is null ? [] : [location],
            });
        }
    }

    private static string EvidenceSeverity(string severity) => severity switch
    {
        "critical" or "high" => ReviewEvidenceSeverities.High,
        "medium" or "low" => ReviewEvidenceSeverities.Warn,
        _ => ReviewEvidenceSeverities.Info,
    };

    private static string VerdictToken(QualityStudioRuleAnalysisVerdict verdict) => verdict switch
    {
        QualityStudioRuleAnalysisVerdict.Passed => "passed",
        QualityStudioRuleAnalysisVerdict.Findings => "findings",
        QualityStudioRuleAnalysisVerdict.Failed => "failed",
        _ => "not-applicable",
    };
}

public sealed record QualityStudioRuleAnalysisRequest(
    string RepositoryPath,
    string JobFolderPath,
    string? TaskType,
    IReadOnlyCollection<string>? Tags,
    string? Title,
    IReadOnlyCollection<string>? ChangedFiles,
    int? RunIndex);

public enum QualityStudioRuleAnalysisVerdict
{
    Passed,
    Findings,
    Failed,
    NotApplicable,
}

public sealed record QualityStudioRuleAnalysisOutcome(
    QualityStudioRuleAnalysisVerdict Verdict,
    string Reason,
    long DurationMs,
    IReadOnlyList<QualityStudioFinding> Findings,
    string ArtifactPath)
{
    public bool RequiresRetry => Verdict is
        QualityStudioRuleAnalysisVerdict.Findings or QualityStudioRuleAnalysisVerdict.Failed;
}

public sealed record QualityStudioCoreResult(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<string> AppliedRuleIds,
    IReadOnlyList<QualityStudioFinding> Findings,
    IReadOnlyList<QualityStudioProvenance> Provenance);

public sealed record QualityStudioFinding(
    string Id,
    string RuleId,
    string Aspect,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    string? Path,
    int? Line,
    int? Column,
    string? SensorId,
    string? ProducerVersion,
    string? Evidence);

public sealed record QualityStudioProvenance(
    string SensorId,
    string SensorVersion,
    string Scope,
    string Target,
    string ScannedAt);

public sealed record QualityStudioAnalysisEvidence(
    [property: System.Text.Json.Serialization.JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string StepId,
    string PackageId,
    string PackageVersion,
    IReadOnlyList<string> QsSourceCommits,
    string ConfigurationPath,
    string RuleConfigurationPath,
    bool FrontendTouching,
    bool BackendTouching,
    IReadOnlyList<string> EnabledSteps,
    string Verdict,
    string Reason,
    long DurationMs,
    IReadOnlyList<string> AnalyzedPaths,
    IReadOnlyList<string> AppliedRuleIds,
    IReadOnlyList<QualityStudioFinding> Findings,
    IReadOnlyList<QualityStudioProvenance> Provenance);
