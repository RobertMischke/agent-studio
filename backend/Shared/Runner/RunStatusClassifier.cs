namespace AgentStudio.Shared;

// RunStopReason now comes from the CodingAgentRunner package (aliased in the csproj).

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
