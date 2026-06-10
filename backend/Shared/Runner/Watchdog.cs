namespace AgentStudio.Shared;

/// <summary>
/// Watchdog state for an active CLI run, derived purely from how long it
/// has been silent and how long ago it started. No process-tree
/// inspection - the silence signal alone is the deciding input. Cross-
/// platform, P/Invoke-free.
/// </summary>
public enum WatchdogState
{
    /// <summary>Streaming output, or run is fresh and still inside the warm-up grace window.</summary>
    Healthy,
    /// <summary>Silent for &gt;= QuietSeconds. UI hint, no backend action.</summary>
    Quiet,
    /// <summary>Silent for &gt;= SuspiciousSeconds. Orchestrator posts a meta message warning of the upcoming kill.</summary>
    Suspicious,
    /// <summary>Silent for &gt;= HungSeconds. Caller kills the process tree.</summary>
    Hung
}

/// <summary>
/// Tunable thresholds for <see cref="Watchdog.DecideState"/>. Loaded from
/// configuration (<c>Watchdog:*</c>); per-CLI overrides allowed via the
/// <c>Watchdog:PerCli:&lt;cliType&gt;</c> section.
/// </summary>
public sealed record WatchdogConfig(
    bool Enabled = true,
    double WarmUpGraceSeconds = 30,
    double QuietSeconds = 30,
    double SuspiciousSeconds = 60,
    double HungSeconds = 120,
    double TickIntervalSeconds = 5)
{
    public static readonly WatchdogConfig Default = new();
}

/// <summary>
/// Pure decision library for the watchdog. Same shape as
/// <see cref="RunPlanner"/> and <see cref="RunOutcomePolicy"/>: inputs in,
/// state out, no side effects. Tested as a matrix in
/// <c>WatchdogTests</c>.
///
/// <para>
/// The silence clock resets every time the CLI streams a real line (not
/// our synthetic <c>[taskboard]</c>, <c>[orchestrator]</c>, or
/// <c>[watchdog]</c> lines). The warm-up grace is a separate clock
/// anchored on run start: even if the CLI is silent, we stay
/// <see cref="WatchdogState.Healthy"/> until the run is older than
/// <see cref="WatchdogConfig.WarmUpGraceSeconds"/>. This avoids false
/// positives on Opus/Sonnet runs that legitimately think for 20-30 s
/// before the first frame.
/// </para>
/// </summary>
public static class Watchdog
{
    /// <summary>
    /// Compute the watchdog state for a run that has been silent for
    /// <paramref name="silenceSeconds"/> and started
    /// <paramref name="runAgeSeconds"/> seconds ago.
    /// </summary>
    public static WatchdogState DecideState(
        double silenceSeconds,
        double runAgeSeconds,
        WatchdogConfig config)
    {
        if (!config.Enabled) return WatchdogState.Healthy;
        if (runAgeSeconds < config.WarmUpGraceSeconds) return WatchdogState.Healthy;

        if (silenceSeconds >= config.HungSeconds)       return WatchdogState.Hung;
        if (silenceSeconds >= config.SuspiciousSeconds) return WatchdogState.Suspicious;
        if (silenceSeconds >= config.QuietSeconds)      return WatchdogState.Quiet;
        return WatchdogState.Healthy;
    }

    /// <summary>
    /// True only on a state transition that warrants a chat meta message
    /// (Quiet, Suspicious, Hung, or back to Healthy after escalation).
    /// Same-state ticks are silent so the chat does not pile up identical
    /// notes.
    /// </summary>
    public static bool ShouldAnnounce(WatchdogState previous, WatchdogState current)
        => previous != current;
}
