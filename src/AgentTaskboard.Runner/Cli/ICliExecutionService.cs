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
        string? thinkingLevel = null,
        string? jobFolderPath = null,
        string? permissionMode = null,
        CancellationToken ct = default);

    /// <summary>
    /// Terminate the live process for <paramref name="taskKey"/>. The
    /// <paramref name="reason"/> flows into <see cref="OrchestratorApi.Services.Runner.RunStatusClassifier"/>
    /// so user pauses, follow-up pause-and-send, and watchdog kills are
    /// reported as <c>status = "stopped"</c> rather than the misleading
    /// <c>status = "failed", exitCode = -1</c> the legacy implementation
    /// produced. Returns false when no process is tracked under that key.
    /// </summary>
    bool Stop(string jobKey, OrchestratorApi.Services.Runner.RunStopReason reason = OrchestratorApi.Services.Runner.RunStopReason.UserStop);
    bool SendInput(string jobKey, string input);

    List<CliOutputLine> GetOutput(string jobKey);

    /// <summary>
    /// Called by the runner once the per-run JSONL has been merged into the
    /// job's durable <c>logs/cli-output.log</c>. The CLI backend should drop
    /// its runtime JSONL so that, after the in-memory buffer is evicted, the
    /// disk-fallback path in <see cref="GetOutput"/> doesn't double up lines
    /// already present in the consolidated log. No-op if the backend doesn't
    /// keep a runtime JSONL.
    /// </summary>
    void DiscardPersistedOutput(string jobKey);

    /// <summary>
    /// Release runtime output handles without deleting the persisted fallback.
    /// The runner calls this before task-folder lane transitions so Windows
    /// directory moves are not blocked by a completed run's retained log store.
    /// </summary>
    void ReleaseOutputResources(string jobKey);

    CliExecution? GetExecution(string jobKey);
    SessionUsage? GetLastUsage(string jobKey);
    bool IsRunningForProject(string rootPath);

    /// <summary>
    /// UTC timestamp of the last <b>real</b> streamed line from this run
    /// (not synthetic taskboard / orchestrator / watchdog markers), or
    /// null if the run is unknown / has finished. The watchdog uses this
    /// to compute silence duration. Should equal
    /// <see cref="CliExecution.StartedAt"/> on a brand-new run before
    /// the first frame arrives.
    /// </summary>
    DateTime? GetLastStreamedAt(string jobKey);

    /// <summary>
    /// Read / write the watchdog state previously announced for this run.
    /// Used by the runner's per-tick announcer to suppress same-state
    /// repeats. Defaults to <see cref="OrchestratorApi.Services.Runner.WatchdogState.Healthy"/>
    /// for unknown runs.
    /// </summary>
    OrchestratorApi.Services.Runner.WatchdogState GetWatchdogState(string jobKey);
    void SetWatchdogState(string jobKey, OrchestratorApi.Services.Runner.WatchdogState state);

    void ReattachOnStartup();

    /// <summary>
    /// Periodic orphan sweep, run on a timer <b>while the backend is up</b>
    /// (unlike <see cref="ReattachOnStartup"/>, which only fires once at boot).
    /// Closes the days-long accumulation gap: when the backend stays up for
    /// days, a finished or crashed run can leave its CLI process tree
    /// (codex / node) alive and holding job-folder handles, wedging the next
    /// lane move. This reaps only process trees the backend no longer tracks
    /// as a live run; a genuinely in-flight run is never touched. Default
    /// no-op so implementations without their own active-process tracking
    /// (and test stubs) opt out cleanly.
    /// </summary>
    void ReapStaleOrphans() { }

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

    /// <summary>
    /// Typed lifecycle events (ADR-0013). Subclasses with an adapter
    /// raise these alongside <see cref="OnOutput"/>; subclasses without
    /// one only fire <see cref="Cli.CliRunEvent.RunStarted"/> and
    /// <see cref="Cli.CliRunEvent.ProcessExited"/> (both come from the
    /// base spawn / monitor flow). The runner uses these to drive the
    /// phase-aware watchdog.
    /// </summary>
    event Action<string, Cli.CliRunEvent>? OnRunEvent;
}
