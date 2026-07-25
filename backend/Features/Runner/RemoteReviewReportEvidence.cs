using System.Text;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Persists the fenced Remote Review report beside the task. The attempt
/// authority remains the canonical protocol record; this Markdown projection
/// makes the report visible and keeps it attached when the task folder moves.
/// </summary>
internal static class RemoteReviewReportEvidence
{
    public static async Task<string> WriteAsync(
        string jobFolder,
        string attemptId,
        string subjectId,
        Contract.ReviewReportRequest request,
        string reportSha256,
        DateTime receivedAt,
        CancellationToken ct)
    {
        var fileName = $"remote-review-grade-{SafeFilePart(attemptId)}.md";
        var path = Path.Combine(jobFolder, fileName);
        var report = Render(
            attemptId,
            subjectId,
            request,
            reportSha256,
            receivedAt);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false), ct);
        return fileName;
    }

    private static string Render(
        string attemptId,
        string subjectId,
        Contract.ReviewReportRequest request,
        string reportSha256,
        DateTime receivedAt)
    {
        var text = new StringBuilder();
        text.AppendLine("---");
        text.AppendLine("type: remote-review-grade");
        text.AppendLine($"attemptId: {Yaml(attemptId)}");
        text.AppendLine($"subjectId: {Yaml(subjectId)}");
        text.AppendLine($"receivedAt: {receivedAt:O}");
        text.AppendLine($"outcome: {Yaml(request.Outcome)}");
        if (!string.IsNullOrWhiteSpace(request.FailureClassification))
            text.AppendLine($"failureClassification: {Yaml(request.FailureClassification)}");
        text.AppendLine($"expectedResultSha: {Yaml(request.Workspace.ExpectedResultSha)}");
        text.AppendLine($"actualHead: {Yaml(request.Workspace.ActualHead)}");
        text.AppendLine($"reportSha256: {Yaml(reportSha256)}");
        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine("# Remote Review Grade");
        text.AppendLine();
        text.AppendLine($"**Outcome:** {request.Outcome}");
        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            text.AppendLine(request.Summary.Trim());
            text.AppendLine();
        }

        text.AppendLine("## Aspect verdicts");
        text.AppendLine();
        if (request.Verdicts.Count == 0)
        {
            text.AppendLine("_No aspect verdicts were supplied._");
        }
        else
        {
            text.AppendLine("| Aspect | Status | Classification | Summary |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var verdict in request.Verdicts)
            {
                text.AppendLine(
                    $"| {Cell(verdict.Aspect)} | {Cell(verdict.Status)} | {Cell(verdict.Classification)} | {Cell(verdict.Summary)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Immutable subject proof");
        text.AppendLine();
        text.AppendLine($"- Repository: `{request.Workspace.RepositoryId}`");
        text.AppendLine($"- Expected result: `{request.Workspace.ExpectedResultSha}`");
        text.AppendLine($"- Materialized result: `{request.Workspace.ActualHead}`");
        text.AppendLine($"- Tree: `{request.Workspace.TreeHash}`");
        text.AppendLine($"- Dirty before: `{request.Workspace.DirtyBefore.ToString().ToLowerInvariant()}`");
        text.AppendLine($"- Dirty after: `{request.Workspace.DirtyAfter.ToString().ToLowerInvariant()}`");
        text.AppendLine($"- Executor: `{request.ExecutorId}`");
        text.AppendLine($"- Fence: `{request.Fence}`");
        text.AppendLine($"- Authority epoch: `{request.AuthorityEpoch}`");
        return text.ToString();
    }

    private static string SafeFilePart(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());

    private static string Yaml(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static string Cell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
