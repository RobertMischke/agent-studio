using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Matrix lock for <see cref="SleepDetector"/>. The detector infers OS suspend
/// duration from the divergence between the wall clock (advances during sleep)
/// and a monotonic clock (frozen during sleep). Both the pure calculation and
/// the stateful baseline-priming are exercised here with fake readings so no
/// real sleep is needed.
/// </summary>
public class SleepDetectorTests
{
    private const double Threshold = SleepDetector.DefaultThresholdSeconds;

    [Theory]
    // No divergence: both clocks moved the same amount -> no sleep.
    [InlineData(5.0, 5.0, null)]
    // Tiny jitter below threshold -> ignored.
    [InlineData(10.0, 5.0, null)]
    [InlineData(59.0, 0.0, null)]
    // Exactly at threshold counts as sleep.
    [InlineData(60.0, 0.0, 60.0)]
    // A real nap: wall advanced 10min, monotonic only ticked 5s -> ~595s asleep.
    [InlineData(605.0, 5.0, 600.0)]
    // Backwards wall movement (NTP step) -> negative gap, ignored.
    [InlineData(0.0, 5.0, null)]
    public void DetectGapSeconds_Matrix(double wallDelta, double monoDelta, double? expected)
    {
        var gap = SleepDetector.DetectGapSeconds(wallDelta, monoDelta, Threshold);
        if (expected is null)
        {
            Assert.Null(gap);
        }
        else
        {
            Assert.NotNull(gap);
            Assert.Equal(expected.Value, gap!.Value, precision: 3);
        }
    }

    [Fact]
    public void Observe_FirstCall_OnlyPrimesBaseline()
    {
        var detector = new SleepDetector();
        // Even a huge wall/monotonic gap on the very first call returns null,
        // because there is no prior reading to diff against.
        var result = detector.Observe(new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        Assert.Null(result);
    }

    [Fact]
    public void Observe_NormalTicks_NoSleepDetected()
    {
        var detector = new SleepDetector();
        var wall = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;

        detector.Observe(wall, mono); // prime

        // Five normal 5s ticks: wall and monotonic advance together.
        for (var i = 0; i < 5; i++)
        {
            wall = wall.AddSeconds(5);
            mono += TimeSpan.FromSeconds(5);
            Assert.Null(detector.Observe(wall, mono));
        }
    }

    [Fact]
    public void Observe_SuspendResume_ReturnsSleepDuration()
    {
        var detector = new SleepDetector();
        var wall = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.FromSeconds(100);

        detector.Observe(wall, mono); // prime

        // The host sleeps ~30 minutes. Wall clock advanced by 1805s; the
        // monotonic clock was frozen during S3 so it only advanced by the 5s
        // the tick loop actually ran.
        wall = wall.AddSeconds(1805);
        mono += TimeSpan.FromSeconds(5);

        var slept = detector.Observe(wall, mono);

        Assert.NotNull(slept);
        Assert.Equal(1800.0, slept!.Value, precision: 0);
    }

    [Fact]
    public void Observe_AfterResume_ResumesNormalDetection()
    {
        var detector = new SleepDetector();
        var wall = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        detector.Observe(wall, mono); // prime

        // Sleep once.
        wall = wall.AddSeconds(1000);
        mono += TimeSpan.FromSeconds(5);
        Assert.NotNull(detector.Observe(wall, mono));

        // Next normal tick must not re-report the same sleep (baseline moved on).
        wall = wall.AddSeconds(5);
        mono += TimeSpan.FromSeconds(5);
        Assert.Null(detector.Observe(wall, mono));
    }

    [Fact]
    public void Observe_CustomThreshold_Honored()
    {
        var detector = new SleepDetector(thresholdSeconds: 10);
        var wall = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var mono = TimeSpan.Zero;
        detector.Observe(wall, mono); // prime

        // A 15s gap is below the 60s default but above this detector's 10s.
        wall = wall.AddSeconds(20);
        mono += TimeSpan.FromSeconds(5);
        var slept = detector.Observe(wall, mono);

        Assert.NotNull(slept);
        Assert.Equal(15.0, slept!.Value, precision: 3);
    }
}
