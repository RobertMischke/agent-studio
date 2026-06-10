namespace AgentStudio.Shared;

/// <summary>
/// Detects host OS suspend/resume (S3 sleep, hibernate, Modern Standby) by
/// comparing two clocks across a single tick: the wall clock
/// (<see cref="DateTime.UtcNow"/>) keeps advancing while the machine is
/// suspended, but a monotonic clock (QPC via <see cref="System.Diagnostics.Stopwatch"/>)
/// freezes. The difference between how far each clock moved is the time the
/// machine spent asleep.
///
/// <para>
/// This matters to the watchdog: silence is measured as
/// <c>wallNow - lastStreamedAt</c>. After a resume the wall clock has jumped
/// forward by the sleep duration, so every active run looks like it has been
/// silent for the whole nap even though no agent actually went quiet. Without
/// correction the very next watchdog tick would classify healthy runs as
/// <see cref="WatchdogState.Hung"/> and kill them. The detected gap is fed
/// back into the runners so they can reset the silence clocks before the
/// watchdog evaluates.
/// </para>
///
/// <para>Pure decision core (<see cref="DetectGapSeconds"/>) plus a thin
/// stateful wrapper (<see cref="Observe"/>) that holds the previous readings.
/// Both are P/Invoke-free and cross-platform; on a host that never sleeps the
/// gap is always below threshold and nothing fires.</para>
/// </summary>
public sealed class SleepDetector
{
    /// <summary>
    /// Default minimum gap, in seconds, before a clock divergence is treated
    /// as a real OS sleep rather than ordinary scheduler jitter or GC pauses.
    /// Chosen well above any plausible tick stall but below a short nap.
    /// </summary>
    public const double DefaultThresholdSeconds = 60;

    private readonly double _thresholdSeconds;
    private bool _primed;
    private DateTime _lastWallUtc;
    private TimeSpan _lastMonotonic;

    public SleepDetector(double thresholdSeconds = DefaultThresholdSeconds)
    {
        _thresholdSeconds = thresholdSeconds;
    }

    /// <summary>
    /// Pure gap calculation. Given how far the wall clock and the monotonic
    /// clock each advanced between two observations, returns the inferred
    /// sleep duration in seconds when it is at least
    /// <paramref name="thresholdSeconds"/>, otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The wall clock advances during suspend; the monotonic clock does not.
    /// So <c>gap = wallDelta - monoDelta</c> approximates the suspended time.
    /// Backwards wall-clock movement (e.g. an NTP step) yields a negative or
    /// tiny gap and is ignored.
    /// </remarks>
    public static double? DetectGapSeconds(
        double wallDeltaSeconds,
        double monoDeltaSeconds,
        double thresholdSeconds)
    {
        var gap = wallDeltaSeconds - monoDeltaSeconds;
        return gap >= thresholdSeconds ? gap : (double?)null;
    }

    /// <summary>
    /// Record the current readings and report any sleep gap that elapsed since
    /// the previous observation. The first call only primes the baseline and
    /// always returns <c>null</c>. Pass a monotonic reading
    /// (<see cref="System.Diagnostics.Stopwatch.Elapsed"/> from a single
    /// long-lived stopwatch) and the matching wall-clock reading.
    /// </summary>
    public double? Observe(DateTime wallUtcNow, TimeSpan monotonicNow)
    {
        if (!_primed)
        {
            _primed = true;
            _lastWallUtc = wallUtcNow;
            _lastMonotonic = monotonicNow;
            return null;
        }

        var wallDelta = (wallUtcNow - _lastWallUtc).TotalSeconds;
        var monoDelta = (monotonicNow - _lastMonotonic).TotalSeconds;

        _lastWallUtc = wallUtcNow;
        _lastMonotonic = monotonicNow;

        return DetectGapSeconds(wallDelta, monoDelta, _thresholdSeconds);
    }
}
