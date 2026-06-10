namespace AgentStudio.Shared;

/// <summary>
/// Pure cumulative-duration arithmetic for the CORE "Agent execution"
/// pipeline step. Sibling of <see cref="CoreRunStepStatusMapper"/>: inputs in,
/// number out, no side effects.
///
/// <para>
/// The bug this guards against (Symptom 2 of the agent-run-metriken report): a
/// multi-attempt task spawns the agent several times, yet all those runs share
/// ONE persistent CORE step in <c>pipeline-execution.json</c> (the production
/// runner never calls <c>PipelineExecutionLog.Complete</c>, so the record stays
/// in-flight and accumulates rather than starting fresh per run). The old code
/// wrote only the LAST run's duration onto that step, so the Overview pipeline
/// row showed ~55s for a task that actually ran five times - while the
/// Overview's separate "Total Duration" surface, which sums every run, showed
/// the real total. The two disagreed.
/// </para>
///
/// <para>
/// The fix carries the step's accumulated duration forward across runs:
/// <see cref="RunDurationMs"/> measures one run, <see cref="Accumulate"/> adds
/// it onto the total persisted by prior runs. Keeping the add in the run-finish
/// path (read current total, add this run) makes the result correct whether or
/// not the run-start write landed - a lost start write leaves the prior total
/// in place, and finish still adds exactly this run.
/// </para>
/// </summary>
public static class CoreRunStepAccumulator
{
    /// <summary>
    /// One run's own duration in milliseconds. Trusts the CLI's reported
    /// duration when it is positive; otherwise falls back to wall-clock
    /// (<paramref name="nowUtc"/> - <paramref name="startedAtUtc"/>). Never
    /// negative, so a backwards clock skew cannot subtract from the total.
    /// </summary>
    public static long RunDurationMs(double? durationSeconds, DateTime startedAtUtc, DateTime nowUtc)
        => durationSeconds is double secs && secs > 0
            ? (long)Math.Round(secs * 1000)
            : Math.Max(0, (long)(nowUtc - startedAtUtc).TotalMilliseconds);

    /// <summary>
    /// Cumulative total across all runs of the CORE step: the duration carried
    /// forward from prior runs plus this run's duration. Both inputs are
    /// clamped to zero so a stray negative (corrupt prior value, clock skew)
    /// can never shrink the running total.
    /// </summary>
    public static long Accumulate(long priorAccumulatedMs, long thisRunMs)
        => Math.Max(0, priorAccumulatedMs) + Math.Max(0, thisRunMs);
}
