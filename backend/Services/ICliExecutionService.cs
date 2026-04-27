using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

/// <summary>
/// Common surface every CLI backend exposes (Copilot, Claude Code, Codex).
/// All implementations are wrapped by <see cref="CliRouter"/> so callers
/// never need to know which CLI executes a given job.
/// </summary>
public interface ICliExecutionService
{
    /// <summary>One of <see cref="CliTypes"/>.</summary>
    string CliType { get; }

    string GetCliPath();
    bool IsAvailable();
    (bool Available, string? Version, string Path) TestCliPath(string? path = null);

    Task<(CliExecution? Execution, string? Error)> StartAsync(
        string jobId,
        string jobKey,
        string prompt,
        string workingDirectory,
        string? sessionName = null,
        bool resumeSession = false,
        string? model = null,
        CancellationToken ct = default);

    bool Stop(string jobKey);
    bool SendInput(string jobKey, string input);

    List<CliOutputLine> GetOutput(string jobKey);
    CliExecution? GetExecution(string jobKey);
    SessionUsage? GetLastUsage(string jobKey);
    bool IsRunningForProject(string rootPath);

    void ReattachOnStartup();

    /// <summary>Returns the set of models the user can select for this CLI.</summary>
    Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>
    /// Returns true if <paramref name="sessionName"/> looks like a session
    /// identifier this CLI can resume. Cross-CLI session names (e.g. a Copilot
    /// slug fed to Claude's <c>-r</c>) used to make the new CLI hang silently;
    /// callers should drop the recorded name and start fresh when this returns
    /// false.
    /// </summary>
    bool IsCompatibleSessionName(string? sessionName);

    event Action<string, CliOutputLine>? OnOutput;
    event Action<string, CliExecution>? OnStarted;
    event Action<string, CliExecution>? OnFinished;
}
