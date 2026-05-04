namespace OrchestratorApi.Services.Supervisor;

/// <summary>
/// Canonical layout for the supervisor's per-project append-only logs and the
/// system-review monitor's outputs. Centralised here so every writer agrees on
/// the same paths.
/// </summary>
/// <remarks>
/// The log root is the watched workspace's <c>logs/</c> directory, NOT the app
/// repository. Supervisor output is project evidence; it lives next to the
/// project, not next to source. The system-review monitor writes under the
/// workspace root rather than per project because its scope is the whole
/// system.
/// </remarks>
public static class SupervisorLogPaths
{
    /// <summary>
    /// Returns the per-project supervisor log directory:
    /// <c>{workspaceRoot}/logs/meta/{projectSlug}/</c>.
    /// </summary>
    public static string ProjectLogDir(string workspaceRoot, string projectSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        return Path.Combine(workspaceRoot, "logs", "meta", projectSlug);
    }

    public static string ObservationsFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectLogDir(workspaceRoot, projectSlug), "observations.jsonl");

    public static string InterventionsFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectLogDir(workspaceRoot, projectSlug), "interventions.jsonl");

    public static string ReasoningFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectLogDir(workspaceRoot, projectSlug), "reasoning.md");

    public static string HeartbeatFile(string workspaceRoot, string projectSlug) =>
        Path.Combine(ProjectLogDir(workspaceRoot, projectSlug), "heartbeat.json");

    /// <summary>
    /// Returns the system-review output directory:
    /// <c>{workspaceRoot}/logs/system-review/</c>.
    /// </summary>
    public static string SystemReviewDir(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "system-review");
    }
}
