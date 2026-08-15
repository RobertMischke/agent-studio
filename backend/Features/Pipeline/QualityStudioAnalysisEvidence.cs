using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

/// <summary>
/// Agent Studio's narrow consumer port for the QS analysis-core package. The
/// production adapter maps these values to and from
/// AgentOrchestrator.CodeQuality types without involving the Quality Studio
/// HTTP API.
/// </summary>
public interface IQualityStudioAnalysisCore
{
    Task<QualityStudioAnalysisResult> RunAsync(
        QualityStudioAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record QualityStudioAnalysisRequest(
    string RepositoryPath,
    string StepId,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> RuleProfiles,
    string RuleConfigurationPath);

public sealed record QualityStudioAnalysisResult(
    bool Available,
    string Producer,
    string ProducerVersion,
    IReadOnlyList<QualityStudioFinding> Findings,
    string? UnavailableReason = null);

public sealed record QualityStudioFinding(
    string Id,
    string RuleId,
    string Aspect,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    IReadOnlyList<QualityStudioFindingLocation> Locations,
    string? Evidence = null);

public sealed record QualityStudioFindingLocation(string Path, int? Line = null, int? Column = null);

public sealed record QualityStudioAnalysisEvidenceDocument(
    int SchemaVersion,
    string StepId,
    string TaskId,
    DateTimeOffset GeneratedAt,
    QualityStudioCardClass CardClass,
    string Producer,
    string ProducerVersion,
    string RuleConfigurationPath,
    bool FindingsBlockPipeline,
    IReadOnlyList<QualityStudioFinding> Findings);

public sealed record QualityStudioEvidenceOutcome(
    string ArtifactPath,
    int FindingCount,
    bool RequiresSteeredRetry);

/// <summary>
/// Persists the transport-neutral QS finding projection in task results and
/// appends each finding to the ordinary review-evidence stream. This is the
/// boundary consumed by the steered-retry decision: non-security findings may
/// request a retry, while security findings remain visible and non-blocking.
/// </summary>
public static class QualityStudioAnalysisEvidence
{
    public const int CurrentSchemaVersion = 1;
    public const string ResultsDirectory = "quality-studio";

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static QualityStudioEvidenceOutcome Persist(
        string jobFolder,
        string taskId,
        string stepId,
        QualityStudioAnalysisSelection selection,
        QualityStudioAnalysisResult result,
        int? runIndex = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!PipelineCatalogue.QualityAnalysisStepIds.Contains(stepId, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown Quality Studio analysis step '{stepId}'.", nameof(stepId));
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Available)
            throw new InvalidOperationException(
                result.UnavailableReason ?? $"Quality Studio analysis '{stepId}' is unavailable.");

        var relativeArtifact = $"results/{ResultsDirectory}/{stepId}.json";
        var resultsDirectory = Path.Combine(TaskPaths.ResultsDir(jobFolder), ResultsDirectory);
        Directory.CreateDirectory(resultsDirectory);
        var path = Path.Combine(resultsDirectory, stepId + ".json");
        var document = new QualityStudioAnalysisEvidenceDocument(
            CurrentSchemaVersion,
            stepId,
            taskId,
            generatedAt ?? DateTimeOffset.UtcNow,
            selection.CardClass,
            result.Producer,
            result.ProducerVersion,
            selection.RuleConfigurationPath,
            FindingsBlockPipeline: QualityStudioAnalysisPolicy.FindingsBlock(stepId),
            result.Findings);
        WriteAtomic(path, JsonSerializer.Serialize(document, WriteOptions) + Environment.NewLine);

        foreach (var finding in result.Findings)
        {
            ReviewEvidenceLog.Append(jobFolder, new ReviewEvidenceEntry
            {
                Id = EvidenceId(finding),
                Source = ReviewEvidenceSources.QualityStudio,
                Severity = EvidenceSeverity(finding.Severity),
                Title = $"{finding.RuleId}: {finding.Title}",
                Body = EvidenceBody(finding),
                CreatedAt = document.GeneratedAt.UtcDateTime,
                RunIndex = runIndex,
                Artifacts = [relativeArtifact],
                FileRefs = finding.Locations.Select(FileReference).Distinct(StringComparer.Ordinal).ToList(),
            });
        }

        return new QualityStudioEvidenceOutcome(
            relativeArtifact,
            result.Findings.Count,
            result.Findings.Count > 0 && QualityStudioAnalysisPolicy.FindingsBlock(stepId));
    }

    private static string EvidenceId(QualityStudioFinding finding)
    {
        var identity = string.IsNullOrWhiteSpace(finding.Fingerprint) ? finding.Id : finding.Fingerprint;
        return "quality-studio:" + identity;
    }

    private static string EvidenceSeverity(string severity) => severity.Trim().ToLowerInvariant() switch
    {
        "critical" or "high" => ReviewEvidenceSeverities.High,
        "medium" => ReviewEvidenceSeverities.Warn,
        _ => ReviewEvidenceSeverities.Info,
    };

    private static string EvidenceBody(QualityStudioFinding finding)
    {
        var body = new StringBuilder();
        body.Append(finding.Description.Trim());
        if (!string.IsNullOrWhiteSpace(finding.Recommendation))
            body.AppendLine().Append("Recommendation: ").Append(finding.Recommendation.Trim());
        if (!string.IsNullOrWhiteSpace(finding.Evidence))
            body.AppendLine().Append("Evidence: ").Append(finding.Evidence.Trim());
        return body.ToString();
    }

    private static string FileReference(QualityStudioFindingLocation location) =>
        location.Line is > 0 ? $"{location.Path}:{location.Line}" : location.Path;

    private static void WriteAtomic(string path, string json)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
