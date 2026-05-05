namespace OrchestratorApi.Services.Drift;

/// <summary>
/// Canonical layout for drift reports. Mirrors
/// <see cref="OrchestratorApi.Services.Analysis.AnalysisReportPaths"/> but
/// keeps drift evidence in its own pile under
/// <c>{workspaceRoot}/logs/drift/{projectSlug}/</c> because Drift is a
/// first-class project dimension, not an Analysis Reports filter
/// (ROADMAP "Drift Control", design-principles "Drift is a scored project
/// dimension").
/// </summary>
public static class DriftReportPaths
{
    public static string ProjectDir(string workspaceRoot, string projectSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        return Path.Combine(workspaceRoot, "logs", "drift", projectSlug);
    }

    public static string IndexFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), "index.jsonl");

    public static string MarkdownFile(string workspaceRoot, string projectSlug, string reportId) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), $"{reportId}.md");

    public static string JsonSidecarFile(string workspaceRoot, string projectSlug, string reportId) =>
        Path.Combine(ProjectDir(workspaceRoot, projectSlug), $"{reportId}.json");
}
