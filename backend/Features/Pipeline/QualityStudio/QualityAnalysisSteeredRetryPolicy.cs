using System.Text;
using System.Text.Json;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Turns structured QS rule findings into one bounded automatic coding retry.
/// The receipt is the prior pipeline attempt containing the named analysis
/// step, so a restart cannot replenish the budget. Security is deliberately
/// excluded: current policy records unfixed security findings but never blocks
/// or retries the pipeline for them.
/// </summary>
public static class QualityAnalysisSteeredRetryPolicy
{
    public const int MaxAutomaticRetries = 1;

    public static QualityAnalysisRetryDecision Decide(
        Contract.ReviewReportRequest report,
        PipelineExecutionRecord? pipeline)
    {
        var command = report.Commands.LastOrDefault(candidate =>
            Contract.ReviewCommandKinds.IsQualityAnalysis(candidate.ExecutionKind)
            && string.Equals(
                candidate.StepId,
                PipelineCatalogue.QualityStaticRulesStepId,
                StringComparison.Ordinal)
            && string.Equals(
                candidate.Aspect,
                QualityAnalysisPolicy.AngularRuleAxis,
                StringComparison.Ordinal)
            && candidate.ExitCode is not 0);
        if (command is null)
            return QualityAnalysisRetryDecision.None("No blocking Angular rule findings were reported.");

        var findings = ReadFindings(report.Artifacts, command.StdoutSha256);
        if (findings.Count == 0)
            return QualityAnalysisRetryDecision.None("The failed analysis carried no structured named findings.");

        var priorRetries = pipeline?.PreviousAttempts.Count(attempt =>
            attempt.Steps.Any(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.QualityStaticRulesStepId,
                    StringComparison.Ordinal)
                && step.Status == PipelineStepStatus.Failed)) ?? 0;
        if (priorRetries >= MaxAutomaticRetries)
        {
            return new QualityAnalysisRetryDecision(
                ShouldRetry: false,
                BudgetExhausted: true,
                Findings: findings,
                FollowUp: string.Empty,
                Reason: "The Quality Studio Angular rule retry budget is exhausted; findings remain visible for human review.");
        }

        return new QualityAnalysisRetryDecision(
            ShouldRetry: true,
            BudgetExhausted: false,
            Findings: findings,
            FollowUp: BuildFollowUp(findings),
            Reason: $"Quality Studio reported {findings.Count} named Angular rule finding(s).");
    }

    private static IReadOnlyList<QualityAnalysisRetryFinding> ReadFindings(
        IReadOnlyList<Contract.ReviewArtifactEvidenceDto> artifacts,
        string digest)
    {
        var artifact = artifacts.LastOrDefault(candidate =>
            string.Equals(candidate.Sha256, digest, StringComparison.OrdinalIgnoreCase));
        if (artifact?.ContentBase64 is null) return [];
        try
        {
            using var document = JsonDocument.Parse(
                Convert.FromBase64String(artifact.ContentBase64));
            if (!document.RootElement.TryGetProperty("findings", out var findings)
                || findings.ValueKind != JsonValueKind.Array)
                return [];
            return findings.EnumerateArray()
                .Select(finding => new QualityAnalysisRetryFinding(
                    RuleId: Text(finding, "ruleId", "unknown-rule"),
                    Title: Text(finding, "title", "Quality finding"),
                    Path: Text(finding, "path", "unknown-path"),
                    Line: finding.TryGetProperty("line", out var line)
                          && line.ValueKind == JsonValueKind.Number
                        ? line.GetInt32()
                        : null,
                    Recommendation: Text(
                        finding,
                        "recommendation",
                        "Apply the named Quality Studio rule.")))
                .ToArray();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return [];
        }
    }

    private static string BuildFollowUp(IReadOnlyList<QualityAnalysisRetryFinding> findings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Quality Studio found named Angular rule violations in the immutable review subject. Fix only these findings, preserve unrelated behavior, and rerun the relevant checks:");
        foreach (var finding in findings)
        {
            builder.AppendLine();
            builder.Append("- `").Append(finding.RuleId).Append("` at `")
                .Append(finding.Path);
            if (finding.Line is not null) builder.Append(':').Append(finding.Line);
            builder.Append("`: ").AppendLine(finding.Title);
            builder.Append("  ").AppendLine(finding.Recommendation);
        }
        builder.AppendLine();
        builder.AppendLine("This is the one automatic Quality Studio rule retry. The rule definitions remain owned by the Quality Studio library; do not rewrite them in this repository.");
        return builder.ToString().TrimEnd();
    }

    private static string Text(JsonElement value, string property, string fallback)
        => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? fallback
            : fallback;
}

public sealed record QualityAnalysisRetryDecision(
    bool ShouldRetry,
    bool BudgetExhausted,
    IReadOnlyList<QualityAnalysisRetryFinding> Findings,
    string FollowUp,
    string Reason)
{
    public static QualityAnalysisRetryDecision None(string reason)
        => new(false, false, [], string.Empty, reason);
}

public sealed record QualityAnalysisRetryFinding(
    string RuleId,
    string Title,
    string Path,
    int? Line,
    string Recommendation);
