namespace AgentStudio.Runner;

/// <summary>
/// Pure decision library for the per-task rapid-crash governor — the missing
/// layer between <see cref="RunQuarantineBreaker"/> (which counts no-progress
/// failures but <i>excludes</i> launch-shaped kinds like
/// <c>CliLaunchFailed</c>) and <see cref="CrossSlugInfraCircuitBreaker"/>
/// (which dedupes a single slug, so one looping task never trips it).
///
/// <para>
/// <b>Why this exists.</b> A task whose CLI exits non-zero within a couple of
/// seconds and committed nothing is a <i>rapid crash</i>. The progress-first
/// picker pulls the same 3-progress folder straight back to the top, so such a
/// task re-starts immediately — observed as ~200 starts in 40 minutes which,
/// together with a build-process leak, exhausted the host and took the backend
/// down (incident 2026-06-07). Neither existing breaker stopped it: the
/// quarantine breaker excluded the launch-shaped kind, and the cross-slug
/// breaker dedupes the single slug by design.
/// </para>
///
/// <para>
/// <b>What it does.</b> Two additive, low-risk effects — it never accepts work
/// or changes routing, it can only delay or feed the existing park route:
/// (1) a rapid crash counts toward the quarantine streak <i>regardless</i> of
/// its issue kind, so a launch-shaped tight-loop is parked in human review via
/// the existing, tested quarantine route; (2) an exponential <see cref="Backoff"/>
/// spaces the retries that precede the park so a crashing task can never
/// saturate the host while the streak accrues.
/// </para>
/// </summary>
public static class RapidCrashBreaker
{
    /// <summary>A failed run shorter than this (seconds) with no commit is "rapid".</summary>
    public const double DefaultFastCrashSeconds = 8.0;

    /// <summary>
    /// True when a finished run is a rapid crash: it failed (non-zero exit and
    /// not a deliberate stop — the caller passes the already-classified
    /// <paramref name="status"/>), finished faster than
    /// <paramref name="fastCrashSeconds"/>, and produced no commit.
    /// </summary>
    public static bool IsRapidCrash(
        string status,
        double durationSeconds,
        int commits,
        double fastCrashSeconds = DefaultFastCrashSeconds)
        => string.Equals(status, RunStatuses.Failed, StringComparison.Ordinal)
           && durationSeconds < fastCrashSeconds
           && commits == 0;

    /// <summary>
    /// Exponential backoff for the Nth consecutive rapid crash (1-based):
    /// 15s, 60s, 240s, … capped at 15 minutes. Returns <see cref="TimeSpan.Zero"/>
    /// for non-positive input. The picker must skip the task until this elapses.
    /// </summary>
    public static TimeSpan Backoff(int consecutiveRapidCrashes)
    {
        if (consecutiveRapidCrashes < 1) return TimeSpan.Zero;
        var exp = Math.Min(consecutiveRapidCrashes - 1, 4); // cap the growth term
        var seconds = Math.Min(15.0 * Math.Pow(4, exp), 900.0);
        return TimeSpan.FromSeconds(seconds);
    }
}
