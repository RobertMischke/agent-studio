namespace OrchestratorApi.Services;

/// <summary>
/// Centralises filesystem layout under a job folder. Every consumer of
/// <c>logs/</c> or <c>cli-output.log</c> should go through here so the
/// magic strings live in one place.
/// </summary>
internal static class TaskPaths
{
    public const string CliOutputLogFileName = "cli-output.log";
    public const string SessionEventsLogFileName = "session-events.jsonl";
    public const string TimelineLogFileName = "timeline.jsonl";
    public const string LogsDirName = "logs";
    public const string RunContextDirName = "run-context";
    public const string ResultsDirName = "results";
    public const string ReviewEvidenceFileName = "review-evidence.jsonl";

    public static string LogsDir(string jobFolder) => Path.Combine(jobFolder, LogsDirName);
    /// <summary>Per-run captured-context files (<c>logs/run-context/&lt;ts&gt;.md</c>); see <c>SessionEvent.ContextRef</c>.</summary>
    public static string RunContextDir(string jobFolder) => Path.Combine(jobFolder, LogsDirName, RunContextDirName);
    public static string CliOutputLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, CliOutputLogFileName);
    public static string SessionEventsLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, SessionEventsLogFileName);
    public static string TimelineLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, TimelineLogFileName);
    public static string ResultsDir(string jobFolder) => Path.Combine(jobFolder, ResultsDirName);
    public static string ReviewEvidenceLog(string jobFolder) => Path.Combine(jobFolder, ResultsDirName, ReviewEvidenceFileName);
}
