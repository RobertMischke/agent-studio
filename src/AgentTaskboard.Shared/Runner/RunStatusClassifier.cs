namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Why a CLI run finished. Carried alongside the OS exit code so the
/// run-status classifier can tell a deliberate kill (user pause, follow-up
/// pause-and-send, silence watchdog, host shutdown) apart from a real
/// process crash. Without this distinction every <see cref="System.Diagnostics.Process.Kill(bool)"/>
/// surfaces as <c>status = "failed", exitCode = -1</c> on Windows, which the
/// frontend then renders as a "Task execution failed with exit code -1"
/// error modal even though the user just clicked Pause &amp; Send.
/// </summary>
public enum RunStopReason
{
    /// <summary>The process exited on its own (success or genuine crash).</summary>
    None = 0,
    /// <summary>User clicked the explicit Pause button.</summary>
    UserStop,
    /// <summary>UI sent a follow-up while the agent was running; backend pauses then continues.</summary>
    FollowupPause,
    /// <summary>Silence watchdog killed the process tree.</summary>
    Watchdog,
    /// <summary>Host is shutting down or the run's cancellation token fired.</summary>
    Cancelled,
    /// <summary>The CLI's user-configured quota cap was exceeded mid-run.</summary>
    QuotaCapExceeded,
    /// <summary>
    /// The agent emitted a typed sentinel ([[TASK_DONE]] / [[TASK_BLOCKED]] /
    /// [[TASK_NEEDS_INPUT]] / [[TASK_NOOP]]) and a TurnCompleted frame, but
    /// the OS process did not exit. Stream-json mode can leave claude-code
    /// alive after the result frame; without this kill, the orchestrator
    /// would wait forever for an exit and never run AgentOutcomeAnalyzer.
    /// Treated as a successful completion by the classifier - the agent did
    /// its job, only the lingering process was killed.
    /// </summary>
    SentinelDetected,
    /// <summary>
    /// The CLI base class detected an OS-level / sandbox-level blocker in
    /// the child's stdout/stderr (see <c>AgentEnvironmentDetector</c>)
    /// and killed the process before the agent could burn the full silence
    /// budget retrying against an unrecoverable host error.
    /// </summary>
    EnvironmentBlocker,
    /// <summary>
    /// Codex stopped emitting frames after a successful tool call but never
    /// sent a closing <c>turn.completed</c> or sentinel. The runner waited
    /// past the silent-completion grace window
    /// (<c>CodexSilentCompletionDetector.DefaultSilenceSeconds</c>), then
    /// killed the still-alive process so the regular post-run pipeline can
    /// run. Treated as a successful completion by the classifier - the
    /// agent likely finished its work, only the sign-off was missing. The
    /// auto-review path tags the job with <c>outcome:silent-finish</c> and
    /// posts an observation event so the case is visible in the lane.
    /// </summary>
    SilentCompletion
}

/// <summary>String constants for <see cref="Models.CliExecution.Status"/>. Persisted; keep stable.</summary>
public static class RunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    /// <summary>Process was deliberately killed (user pause, follow-up pause-and-send, watchdog, host shutdown). Not a crash.</summary>
    public const string Stopped = "stopped";
}

/// <summary>
/// Pure decision library that maps <c>(exitCode, stopReason)</c> to the
/// final run status. Same shape as <c>Watchdog</c> and
/// <c>RunCompletionPolicy</c>: inputs in, status out, no side
/// effects. Tested as a matrix in <c>RunStatusClassifierTests</c>.
///
/// <para>
/// Load-bearing rule: any non-<see cref="RunStopReason.None"/> reason wins
/// over the exit code. Process.Kill on Windows hands back exit code -1
/// regardless of intent, so the only honest signal of "we killed this on
/// purpose" lives in the reason field. Treating that as "failed" leaks
/// backend internals (kill mechanics) into the user's UX as a false-alarm
/// crash modal.
/// </para>
/// </summary>
public static class RunStatusClassifier
{
    public static string Classify(int? exitCode, RunStopReason reason)
    {
        // SentinelDetected means the agent finished its work and emitted a
        // typed sentinel; we killed only the lingering process. Treat as a
        // successful completion regardless of the kill-induced exit code.
        if (reason == RunStopReason.SentinelDetected) return RunStatuses.Completed;
        // SilentCompletion: Codex's post-tool-call hang shape. The work is
        // already on disk; the missing sign-off is a CLI behaviour quirk,
        // not a run failure. Route through the same Completed lane so the
        // regular post-run pipeline (auto-review aspect calls + lane move)
        // runs as if the CLI had exited cleanly.
        if (reason == RunStopReason.SilentCompletion) return RunStatuses.Completed;
        if (reason != RunStopReason.None) return RunStatuses.Stopped;
        return exitCode == 0 ? RunStatuses.Completed : RunStatuses.Failed;
    }
}
