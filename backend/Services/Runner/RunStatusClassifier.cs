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
    QuotaCapExceeded
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
/// final run status. Same shape as <see cref="Watchdog"/> and
/// <see cref="RunCompletionPolicy"/>: inputs in, status out, no side
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
        if (reason != RunStopReason.None) return RunStatuses.Stopped;
        return exitCode == 0 ? RunStatuses.Completed : RunStatuses.Failed;
    }
}
