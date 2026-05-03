using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Per-phase silence budgets. Different phases tolerate different silences:
/// the spawn -&gt; first-event window must be tight (a fast handshake) while
/// a tool-execution window may legitimately last several minutes (a Bash
/// build, a slow grep over a large repo). The original silence-only
/// watchdog ([Watchdog]) treats all silences the same; that produced
/// false positives during long tool runs and false negatives on the
/// "init then nothing" hang shape.
///
/// <para>
/// The numbers below are deliberate: they map to user-observable
/// behaviour rather than to any one CLI's internal pacing. Adjust via
/// <c>Watchdog:Phase:&lt;phase&gt;:&lt;Suspicious|Hung&gt;Seconds</c> in
/// configuration when a per-CLI calibration is needed.
/// </para>
/// </summary>
public sealed record PhaseBudget(
    double SuspiciousSeconds,
    double HungSeconds)
{
    public static PhaseBudget For(RunPhase phase) => phase switch
    {
        // Spawn handshake should be near-instant. Anything past 30 s
        // means the CLI binary is wedged or the OS pipe is broken; past
        // 60 s the runner kills it.
        RunPhase.Spawning             => new PhaseBudget(SuspiciousSeconds: 30,  HungSeconds: 60),
        RunPhase.SessionInitializing  => new PhaseBudget(SuspiciousSeconds: 30,  HungSeconds: 60),
        // After SessionStarted but before TurnStarted, the CLI has the
        // prompt and is contacting the model. The original "init then
        // silence" hang sits here: the symptom we want to surface fast.
        RunPhase.PromptConsumed       => new PhaseBudget(SuspiciousSeconds: 60,  HungSeconds: 180),
        // Inside a turn we expect output deltas every few seconds; one
        // minute of silence between deltas is the upper bound, two
        // minutes triggers a kill.
        RunPhase.TurnInProgress       => new PhaseBudget(SuspiciousSeconds: 60,  HungSeconds: 180),
        RunPhase.OutputDelta          => new PhaseBudget(SuspiciousSeconds: 60,  HungSeconds: 180),
        // Tool execution legitimately runs longer than ordinary turns
        // (Bash builds, grep over big repos, web fetches). Be generous.
        RunPhase.ToolExecuting        => new PhaseBudget(SuspiciousSeconds: 180, HungSeconds: 600),
        // Terminal-for-this-turn states - the watchdog stays its hand
        // because the runner is about to finalize anyway.
        RunPhase.TurnCompleted        => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.TurnFailed           => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.NeedsInput           => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.Exited               => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.Killed               => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        // Adapter could not classify. Use the most defensive budget so
        // a CLI we cannot read still gets killed eventually, but with
        // enough margin that an experimental CLI is not killed mid-turn.
        RunPhase.Unknown              => new PhaseBudget(SuspiciousSeconds: 60, HungSeconds: 240),
        _                              => new PhaseBudget(SuspiciousSeconds: 60, HungSeconds: 180)
    };
}

/// <summary>
/// Phase-aware extension of <see cref="Watchdog"/>. Same pure-function
/// shape; the difference is the budget comes from <see cref="PhaseBudget.For(RunPhase)"/>
/// rather than a single global threshold.
///
/// <para>
/// The original watchdog still works for CLIs we have not yet adapted
/// to <see cref="CliRunEvent"/>; this one takes over when the runner has
/// a known phase. The per-phase reasoning makes the chat meta message
/// dramatically more useful: instead of "agent silent 60 s" the user
/// sees "agent silent 60 s during ToolExecuting (allowed: 180 s) - the
/// tool may legitimately be running" or "agent silent 60 s during
/// PromptConsumed (allowed: 60 s) - we have not seen a turn start;
/// likely stuck on the API or the CLI's session DB".
/// </para>
/// </summary>
public static class PhaseAwareWatchdog
{
    /// <summary>
    /// Compute the watchdog state for a run that has been silent for
    /// <paramref name="silenceSeconds"/> and is currently in <paramref name="phase"/>.
    /// The warm-up grace is anchored on run start (same as the legacy
    /// watchdog) - we never escalate during warmup, regardless of phase.
    /// </summary>
    public static WatchdogState DecideState(
        double silenceSeconds,
        double runAgeSeconds,
        RunPhase phase,
        WatchdogConfig config)
    {
        if (!config.Enabled) return WatchdogState.Healthy;
        if (runAgeSeconds < config.WarmUpGraceSeconds) return WatchdogState.Healthy;

        var budget = PhaseBudget.For(phase);
        if (silenceSeconds >= budget.HungSeconds)       return WatchdogState.Hung;
        if (silenceSeconds >= budget.SuspiciousSeconds) return WatchdogState.Suspicious;
        // Quiet level still uses the global QuietSeconds for a soft
        // first-warning - per-phase Quiet would over-fragment the UI
        // signal without adding diagnostic value.
        if (silenceSeconds >= config.QuietSeconds)      return WatchdogState.Quiet;
        return WatchdogState.Healthy;
    }

    /// <summary>
    /// One-line summary the runner inserts into the chat meta line so
    /// the user sees WHY a state change happened. Budgets are baked in
    /// so the message reads as evidence, not policy.
    /// </summary>
    public static string FormatBudgetReason(RunPhase phase, double silenceSeconds)
    {
        var budget = PhaseBudget.For(phase);
        return $"phase={phase} silence={silenceSeconds:F0}s allowed={budget.SuspiciousSeconds:F0}/{budget.HungSeconds:F0}s";
    }
}
