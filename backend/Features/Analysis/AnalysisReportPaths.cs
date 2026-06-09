namespace OrchestratorApi.Services.Analysis;

/// <summary>
/// Canonical layout for analysis reports under the watched workspace's
/// <c>logs/</c> directory. One report = one Markdown file plus one JSON
/// sidecar with the same stem. Workspace-scoped reports use the synthetic
/// project key <see cref="WorkspaceProjectKey"/> so a single
/// <see cref="AnalysisReportStore"/> projection covers them too.
/// </summary>
/// <remarks>
/// The log root is the watched workspace, NOT the app repository. Analysis
/// reports are project evidence; they live next to the project, not next to
/// source. Mirrors the convention in
/// <see cref="OrchestratorApi.Services.Supervisor.SupervisorLogPaths"/>.
/// </remarks>
public static class AnalysisReportPaths
{
    /// <summary>
    /// Synthetic project key used for workspace-scoped analysis reports. The
    /// store treats it like any other project key so consumers do not need a
    /// separate code path for workspace reports.
    /// </summary>
    public const string WorkspaceProjectKey = "_workspace";

    /// <summary>
    /// Returns the per-project analysis-report directory:
    /// <c>{workspaceRoot}/logs/analysis/{projectSlug}/</c>. For workspace-
    /// scoped reports pass <see cref="WorkspaceProjectKey"/>.
    /// </summary>
    public static string ProjectDir(string workspaceRoot, string projectSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        return Path.Combine(workspaceRoot, "logs", "analysis", projectSlug);
    }

    /// <summary>
    /// Append-only JSONL projection of every JSON sidecar landed for this
    /// (workspace, project) pair. The store appends one line per report on
    /// write; the per-report JSON sidecar files coexist for human inspection
    /// and external monitor consumption.
    /// </summary>
    public static string IndexFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), "index.jsonl");

    /// <summary>
    /// Markdown sibling for one report.
    /// </summary>
    public static string MarkdownFile(string workspaceRoot, string projectSlug, string reportId) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), $"{reportId}.md");

    /// <summary>
    /// JSON sidecar for one report. Same stem as the Markdown sibling so
    /// consumers find the sidecar by direct lookup.
    /// </summary>
    public static string JsonSidecarFile(string workspaceRoot, string projectSlug, string reportId) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), $"{reportId}.json");
}
