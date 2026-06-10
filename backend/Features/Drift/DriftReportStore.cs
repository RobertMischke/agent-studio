using System.Text;
using System.Text.Json;

namespace AgentStudio.Drift;

/// <summary>
/// File-backed in-memory projection of one project's drift-report folder.
/// Mirrors <see cref="AgentStudio.Analysis.AnalysisReportStore"/>:
/// every <see cref="AppendAsync"/> writes the durable Markdown sibling, the
/// JSON sidecar (always for drift reports - the structured shape is the
/// triage signal), and appends the same record to the per-project index for
/// fast list queries.
/// </summary>
/// <remarks>
/// Reports are append-only and immutable once written; corrections land as a
/// new report. Storage layout is documented in
/// <see cref="DriftReportPaths"/>.
/// </remarks>
public sealed class DriftReportStore : InMemoryStore<DriftReport>
{
    protected override string ResolvePath(string workspaceRoot, string project)
        => DriftReportPaths.IndexFile(workspaceRoot, project);

    protected override string GetId(DriftReport item) => item.ReportId;

    protected override bool TryValidate(DriftReport item, out string? error)
        => DriftReportValidator.TryValidate(item, out error);

    /// <summary>
    /// Append one report to the index, write the Markdown sibling, and write
    /// the JSON sidecar. Markdown is the durable human artifact: a report
    /// without Markdown would not survive a parse failure, so the body is
    /// required.
    /// </summary>
    public async Task<long> AppendAsync(
        string workspaceRoot,
        string project,
        DriftReport report,
        string markdownBody,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(markdownBody))
        {
            throw new InvalidOperationException(
                "DriftReport rejected: markdownBody required (Markdown is the durable human artifact).");
        }

        var dir = DriftReportPaths.ProjectDir(workspaceRoot, project);
        Directory.CreateDirectory(dir);

        var markdownPath = DriftReportPaths.MarkdownFile(workspaceRoot, project, report.ReportId);
        await File.WriteAllTextAsync(markdownPath, markdownBody, Encoding.UTF8, ct).ConfigureAwait(false);

        var sidecarPath = DriftReportPaths.JsonSidecarFile(workspaceRoot, project, report.ReportId);
        var sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        await File.WriteAllBytesAsync(sidecarPath, sidecarBytes, ct).ConfigureAwait(false);

        return await base.AppendAsync(workspaceRoot, project, report, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the Markdown sibling for one report. Returns null when no report
    /// with that id exists in the projection or when the Markdown file is
    /// missing.
    /// </summary>
    public string? ReadMarkdown(string workspaceRoot, string project, string reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId)) return null;
        var record = GetById(workspaceRoot, project, reportId);
        if (record is null) return null;

        var path = DriftReportPaths.MarkdownFile(workspaceRoot, project, reportId);
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }
}
