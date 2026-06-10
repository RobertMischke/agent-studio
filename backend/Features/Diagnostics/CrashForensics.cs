namespace AgentStudio.Diagnostics;

/// <summary>
/// How the <i>previous</i> backend run ended, decided at the next boot by
/// comparing the markers each run leaves behind.
/// </summary>
public enum PreviousRunVerdict
{
    /// <summary>No prior <c>startup.json</c> — first boot in this log directory.</summary>
    FirstBoot,

    /// <summary>The previous run wrote a <c>last-shutdown.json</c> after it started: a clean CLR teardown (Ctrl+C, SIGTERM, polite shutdown).</summary>
    GracefulShutdown,

    /// <summary>A <c>last-crash.json</c> was recorded after the previous run started but no shutdown marker followed: a managed exception ended it.</summary>
    ManagedCrash,

    /// <summary>
    /// The previous run started but left <b>neither</b> a shutdown nor a crash
    /// marker. The in-process handlers never ran, so this is the silent class:
    /// StackOverflowException (uncatchable), an OS OOM-kill, a native PTY crash,
    /// or an external <c>Process.Kill</c>. This is the "the log just stops"
    /// disappearance the host-stability work exists to surface.
    /// </summary>
    SilentKill,
}

/// <summary>
/// Outcome of a boot-time previous-run classification, including the raw
/// marker timestamps that drove the verdict so an operator (or the
/// diagnostics endpoint) can see the evidence, not just the conclusion.
/// </summary>
public sealed record PreviousRunReport(
    PreviousRunVerdict Verdict,
    DateTime? PreviousStartedAt,
    int? PreviousPid,
    DateTime? LastShutdownAt,
    DateTime? LastCrashAt);

/// <summary>
/// Pure decision library for "how did the previous backend run end?". Kept
/// free of any filesystem access so the verdict logic is unit-testable; the
/// marker I/O lives in <see cref="CrashRecorder"/>.
///
/// <para>
/// The in-process safety nets (<c>AppDomain.UnhandledException</c>,
/// <c>TaskScheduler.UnobservedTaskException</c>, the <c>ProcessExit</c>
/// shutdown marker) can only witness a <i>managed</i> death. A
/// StackOverflowException, an OS OOM-kill, or a native crash terminates the
/// process before any of them run, leaving the api-log to simply stop. By
/// having every run write a startup marker on boot and a shutdown marker on
/// graceful exit, the <i>next</i> boot can diff the two against the crash
/// marker and name the silent class instead of leaving it invisible.
/// </para>
/// </summary>
public static class CrashForensics
{
    /// <summary>
    /// Classify the previous run from three marker timestamps (all UTC, all
    /// nullable when the marker is absent).
    /// </summary>
    /// <param name="previousStartedAt">capturedAt of the previous run's startup marker, or null on first boot.</param>
    /// <param name="lastShutdownAt">capturedAt of the most recent shutdown marker, or null.</param>
    /// <param name="lastCrashAt">capturedAt of the most recent crash marker, or null.</param>
    public static PreviousRunVerdict Classify(
        DateTime? previousStartedAt,
        DateTime? lastShutdownAt,
        DateTime? lastCrashAt)
    {
        if (previousStartedAt is null) return PreviousRunVerdict.FirstBoot;

        // A marker only belongs to the previous run if it was written at or
        // after that run started. An older marker left by a run two boots ago
        // must not mask a fresh silent death.
        var hadShutdown = lastShutdownAt.HasValue && lastShutdownAt.Value >= previousStartedAt.Value;
        var hadCrash = lastCrashAt.HasValue && lastCrashAt.Value >= previousStartedAt.Value;

        if (hadShutdown) return PreviousRunVerdict.GracefulShutdown;
        if (hadCrash) return PreviousRunVerdict.ManagedCrash;
        return PreviousRunVerdict.SilentKill;
    }
}
