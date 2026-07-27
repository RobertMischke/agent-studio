namespace AgentStudio.Shared;

/// <summary>
/// Server-owned admission policy for one execution host (AGT-2302 / AGT-2376).
///
/// <para>
/// Capacity is a host-level fact, not a project-level one: every project a host
/// claims for shares the same ceiling. The three operator-visible knobs are
/// </para>
/// <list type="bullet">
///   <item><description><b>Ceiling</b> - the hard cap on concurrent runs.</description></item>
///   <item><description><b>Target load</b> - the CPU percentage above which the
///     host stops taking on <em>additional</em> work.</description></item>
///   <item><description><b>Ramp</b> - how fast concurrency is allowed to grow
///     once work is already running: <c>conservative</c> paces admissions one per
///     minute, <c>balanced</c> (the default, and today's behaviour) fills the
///     ceiling as fast as claims arrive, and <c>aggressive</c> additionally
///     ignores the target-load brake.</description></item>
/// </list>
///
/// <para>
/// The first run is always admitted (subject to the ceiling) so an idle host
/// never sits still because of a stale load sample or a ramp interval. Target
/// load and ramp only shape the <em>growth</em> of concurrency.
/// </para>
///
/// <para>
/// Pure logic with an injected clock so admission is deterministically testable;
/// the claim endpoint supplies the facts and applies the verdict.
/// </para>
/// </summary>
public static class HostCapacityPolicy
{
    /// <summary>Ceiling used when neither the host nor a runner reported one.</summary>
    public const int DefaultMaxParallelism = 2;

    /// <summary>Target CPU load used when the operator never set one.</summary>
    public const int DefaultTargetLoadPercent = 80;

    public const int MinMaxParallelism = 1;
    public const int MaxMaxParallelism = 256;
    public const int MinTargetLoadPercent = 50;
    public const int MaxTargetLoadPercent = 95;

    /// <summary>Clamp an operator-supplied ceiling into the supported range.</summary>
    public static int ClampCeiling(int value)
        => Math.Clamp(value, MinMaxParallelism, MaxMaxParallelism);

    /// <summary>Clamp an operator-supplied target load into the supported range.</summary>
    public static int ClampTargetLoad(int value)
        => Math.Clamp(value, MinTargetLoadPercent, MaxTargetLoadPercent);

    /// <summary>
    /// Resolve the ceiling that governs this claim, in precedence order: the
    /// host target set centrally, otherwise the largest of the daemon's own
    /// <c>RUNNER_MAX_PARALLELISM</c> and the deprecated per-project
    /// <c>maxParallelism</c> opt-in.
    ///
    /// <para>
    /// Returns <c>null</c> when nothing is known. The server then does not
    /// enforce a ceiling at all: inventing one would be a silent throttle, and
    /// a fleet that never reported its capacity must keep behaving as before.
    /// The host row shows "capacity not reported" for exactly this state.
    /// </para>
    ///
    /// <para>
    /// DEPRECATED COMPAT PATH: <paramref name="projectCompatCeiling"/> exists
    /// only so hosts that have never been given a central target keep the
    /// parallelism their projects opted into. A value of 1 is the sequential
    /// default and carries no opinion. Remove the parameter and its call site
    /// after 2026-10-01, once every host carries a target.
    /// </para>
    /// </summary>
    public static int? ResolveCeiling(
        int? hostCeiling,
        int? projectCompatCeiling,
        int? bootstrapCeiling)
    {
        if (hostCeiling is > 0) return ClampCeiling(hostCeiling.Value);
        var known = Math.Max(
            projectCompatCeiling is > 1 ? projectCompatCeiling.Value : 0,
            bootstrapCeiling is > 0 ? bootstrapCeiling.Value : 0);
        return known > 0 ? ClampCeiling(known) : null;
    }

    /// <summary>Free slots below the ceiling. Never negative, never above the ceiling.</summary>
    public static int FreeSlots(int ceiling, int activeRuns)
        => Math.Max(0, ClampCeiling(ceiling) - Math.Max(0, activeRuns));

    /// <summary>
    /// Minimum spacing between two admissions on the same host. Only the
    /// conservative strategy paces growth; balanced (the default) keeps the
    /// long-standing behaviour of filling the ceiling as fast as claims arrive,
    /// so adopting host capacity does not silently slow an existing fleet.
    /// </summary>
    public static TimeSpan RampInterval(string? rampStrategy)
        => RunnerRampStrategies.Normalize(rampStrategy) == RunnerRampStrategies.Conservative
            ? TimeSpan.FromSeconds(60)
            : TimeSpan.Zero;

    /// <summary>
    /// Whether the target-load brake applies. An aggressive host accepts work up
    /// to its ceiling regardless of load; the other strategies stop growing once
    /// the host is hotter than its target.
    /// </summary>
    public static bool HonoursTargetLoad(string? rampStrategy)
        => RunnerRampStrategies.Normalize(rampStrategy) != RunnerRampStrategies.Aggressive;

    /// <summary>Decide whether this host may take on one more run right now.</summary>
    public static HostAdmissionVerdict Decide(HostCapacityTargets targets, HostAdmissionFacts facts)
    {
        var ceiling = ClampCeiling(targets.MaxParallelism);
        var activeRuns = Math.Max(0, facts.ActiveRuns);

        if (activeRuns >= ceiling)
            return new HostAdmissionVerdict(
                false,
                HostAdmissionReasons.CeilingReached,
                $"host ceiling reached ({activeRuns}/{ceiling} slots occupied)");

        // An idle host is never held back: neither a stale load sample nor a
        // ramp interval may leave a configured host doing nothing.
        if (activeRuns == 0)
            return new HostAdmissionVerdict(true, HostAdmissionReasons.Admitted, $"first slot of {ceiling}");

        var targetLoad = ClampTargetLoad(targets.TargetLoadPercent);
        if (HonoursTargetLoad(targets.RampStrategy) && facts.CpuPercent is { } cpu && cpu > targetLoad)
            return new HostAdmissionVerdict(
                false,
                HostAdmissionReasons.TargetLoadExceeded,
                $"host load {cpu:0}% is above the {targetLoad}% target with {activeRuns} run(s) active");

        var interval = RampInterval(targets.RampStrategy);
        if (interval > TimeSpan.Zero && facts.LastAdmissionAt is { } last)
        {
            var elapsed = facts.Now.ToUniversalTime() - last.ToUniversalTime();
            if (elapsed < interval)
                return new HostAdmissionVerdict(
                    false,
                    HostAdmissionReasons.RampLimited,
                    $"{RunnerRampStrategies.Normalize(targets.RampStrategy)} ramp admits one run every " +
                    $"{interval.TotalSeconds:0}s; last admission was {Math.Max(0, elapsed.TotalSeconds):0}s ago");
        }

        return new HostAdmissionVerdict(
            true,
            HostAdmissionReasons.Admitted,
            $"slot {activeRuns + 1} of {ceiling}");
    }
}

/// <summary>The three central capacity targets of one host.</summary>
public sealed record HostCapacityTargets(
    int MaxParallelism,
    int TargetLoadPercent,
    string RampStrategy);

/// <summary>
/// Measured facts about the host at claim time. <see cref="ActiveRuns"/> is the
/// server's own lease count, not a daemon-reported observation, so two daemon
/// processes on one host cannot each spend the full ceiling.
/// </summary>
public sealed record HostAdmissionFacts(
    int ActiveRuns,
    DateTime Now,
    DateTime? LastAdmissionAt = null,
    double? CpuPercent = null);

public static class HostAdmissionReasons
{
    public const string Admitted = "admitted";
    /// <summary>Nothing to enforce: the host has no known ceiling.</summary>
    public const string NoCentralCeiling = "no-central-ceiling";
    public const string CeilingReached = "host-ceiling-reached";
    public const string TargetLoadExceeded = "host-target-load-exceeded";
    public const string RampLimited = "host-ramp-limited";
}

/// <summary>One admission decision with a reason code and an operator-readable detail.</summary>
public sealed record HostAdmissionVerdict(bool Admitted, string ReasonCode, string Detail);
