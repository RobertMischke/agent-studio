namespace OrchestratorApi.Services;

/// <summary>
/// Centralises filesystem layout under a job folder. Every consumer of
/// <c>logs/</c> or <c>cli-output.log</c> should go through here so the
/// magic strings live in one place.
/// </summary>
internal static class JobPaths
{
    public const string CliOutputLogFileName = "cli-output.log";
    public const string SessionEventsLogFileName = "session-events.jsonl";
    public const string LogsDirName = "logs";

    public static string LogsDir(string jobFolder) => Path.Combine(jobFolder, LogsDirName);
    public static string CliOutputLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, CliOutputLogFileName);
    public static string SessionEventsLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, SessionEventsLogFileName);
}
