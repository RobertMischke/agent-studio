using System.Text;
using System.Text.Json;

namespace AgentStudio.Analysis;

/// <summary>
/// Typed, file-backed in-memory projection of one project's analysis-report
/// folder. Extends <see cref="InMemoryStore{T}"/> over the per-project
/// append-only index file at
/// <c>logs/analysis/&lt;project&gt;/index.jsonl</c>; in addition, every
/// <see cref="AppendAsync"/> writes the human-readable Markdown sibling and,
/// when present, the JSON sidecar file under the same directory.
/// </summary>
/// <remarks>
/// <para>
/// Schema: <c>docs/system/schemas/analysis-report.schema.json</c>. The contract,
/// producer model, and parse-failure semantics are documented in
/// <c>docs/system/reports/analysis-reports.md</c>.
/// </para>
/// <para>
/// Storage shape: one JSON record per report on the index for fast load and
/// query, plus one Markdown file (always) and one JSON sidecar file (when
/// the producer wrote structured output) so the artifacts are readable on
/// disk by humans, the system-review monitor, and the companion app without
/// going through the backend. The two file pairs and the index always agree
/// at write time; lenient reads handle out-of-band edits via
/// <see cref="InMemoryStore{T}.InvalidateProjection"/>.
/// </para>
/// <para>
/// Reports are append-only and immutable once written. Mistakes are corrected
/// by a follow-up report, not by editing the original. This matches the
/// supervisor advisory store and the Agent Message Bus store.
/// </para>
/// <para>
/// Migration note: the Task Access Layer (ADR-0024) is in phase 1 (contract
/// only) at the time of writing, so this store does not call
/// <c>TaskScannerService</c> or write to <c>task.json</c>. Follow-up task
/// creation lives outside this store; this layer only carries the typed
/// suggestion. When the Task Access Layer ships its mutation phase, the
/// callers that turn a <see cref="AnalysisReportFollowUpTaskSuggestion"/>
/// into a real queued job will go through <c>ITaskAccess.Create</c>.
/// </para>
/// </remarks>
public sealed class AnalysisReportStore : InMemoryStore<AnalysisReport>
{
    protected override string ResolvePath(string workspaceRoot, string project)
        => AnalysisReportPaths.IndexFile(workspaceRoot, project);

    protected override string GetId(AnalysisReport item) => item.ReportId;

    protected override bool TryValidate(AnalysisReport item, out string? error)
        => AnalysisReportValidator.TryValidate(item, out error);

    /// <summary>
    /// Append one report to the index, write the Markdown sibling, and write
    /// the JSON sidecar when the producer supplied structured output.
    /// </summary>
    /// <param name="workspaceRoot">Watched workspace root.</param>
    /// <param name="project">Project slug; pass
    /// <see cref="AnalysisReportPaths.WorkspaceProjectKey"/> for workspace-
    /// scoped reports.</param>
    /// <param name="report">The structured record.</param>
    /// <param name="markdownBody">The durable human artifact. Must be non-
    /// empty: the Markdown is the load-bearing contract; a report without
    /// Markdown would not survive a parse failure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new monotonic projection version.</returns>
    public async Task<long> AppendAsync(
        string workspaceRoot,
        string project,
        AnalysisReport report,
        string markdownBody,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(markdownBody))
        {
            throw new InvalidOperationException(
                "AnalysisReport rejected: markdownBody required (Markdown is the durable human artifact).");
        }

        var dir = AnalysisReportPaths.ProjectDir(workspaceRoot, project);
        Directory.CreateDirectory(dir);

        // Write the human-readable Markdown sibling first. If the index
        // append fails afterwards we still have the human artifact on disk.
        var markdownPath = AnalysisReportPaths.MarkdownFile(workspaceRoot, project, report.ReportId);
        await File.WriteAllTextAsync(markdownPath, markdownBody, Encoding.UTF8, ct).ConfigureAwait(false);

        // Write the JSON sidecar when the producer reported structured
        // output. Unstructured and MalformedJson reports do not get a
        // sidecar; the Markdown remains the only artifact and the parse
        // status records why.
        if (report.ParseStatus == AnalysisReportParseStatus.Structured)
        {
            var sidecarPath = AnalysisReportPaths.JsonSidecarFile(workspaceRoot, project, report.ReportId);
            var sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            await File.WriteAllBytesAsync(sidecarPath, sidecarBytes, ct).ConfigureAwait(false);
        }

        // Append the same record to the per-project index so
        // InMemoryStore<T>'s snapshot / cursor / by-id surface picks it up.
        return await base.AppendAsync(workspaceRoot, project, report, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the Markdown sibling for one report. Returns null when no report
    /// with that id exists in the projection or when the Markdown file is
    /// missing.
    /// </summary>
    /// <remarks>
    /// The Markdown body is intentionally not held in memory: it is the
    /// durable human artifact, accessed by drill-down rather than by every
    /// list query. Consumers that render lists pull
    /// <see cref="AnalysisReport.Summary"/> from the structured record.
    /// </remarks>
    public string? ReadMarkdown(string workspaceRoot, string project, string reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId)) return null;
        var record = GetById(workspaceRoot, project, reportId);
        if (record is null) return null;

        var path = AnalysisReportPaths.MarkdownFile(workspaceRoot, project, reportId);
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }
}
