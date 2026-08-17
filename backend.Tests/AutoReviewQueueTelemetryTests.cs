using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewQueueTelemetryTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Summarize_EmptyWindow_ReturnsZeroRateAndNullMedian()
    {
        var summary = AutoReviewQueueTelemetry.Summarize([], Now, TimeSpan.FromMinutes(30));

        Assert.Equal(0, summary.DrainRatePerMinute);
        Assert.Null(summary.MedianDurationMs);
        Assert.Equal(0, summary.SampleCount);
    }

    [Fact]
    public void Summarize_ExcludesSamplesOutsideTheTrailingWindow()
    {
        var samples = new[]
        {
            Sample(Now.AddMinutes(-45), 1000, drained: true),
            Sample(Now.AddMinutes(-10), 2000, drained: true),
            Sample(Now.AddMinutes(-5), 3000, drained: true),
        };

        var summary = AutoReviewQueueTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(30));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2500, summary.MedianDurationMs);
    }

    [Fact]
    public void Summarize_DeferredSamplesCountTowardDurationButNotDrainRate()
    {
        var samples = new[]
        {
            Sample(Now.AddMinutes(-5), 1000, drained: true),
            Sample(Now.AddMinutes(-5), 3000, drained: false),
        };

        var summary = AutoReviewQueueTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2000, summary.MedianDurationMs);
        // Only one of the two samples drained the queue; the other re-enters it.
        Assert.Equal(0.1, summary.DrainRatePerMinute, precision: 3);
    }

    [Fact]
    public void Summarize_MedianOfOddCountIsTheMiddleValue()
    {
        var samples = new[] { 3000.0, 1000.0, 2000.0 }
            .Select(ms => Sample(Now.AddMinutes(-1), ms, drained: true))
            .ToArray();

        var summary = AutoReviewQueueTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2000, summary.MedianDurationMs);
    }

    [Fact]
    public void Summarize_MedianOfEvenCountAveragesTheTwoMiddleValues()
    {
        var samples = new[] { 4000.0, 1000.0, 2000.0, 3000.0 }
            .Select(ms => Sample(Now.AddMinutes(-1), ms, drained: true))
            .ToArray();

        var summary = AutoReviewQueueTelemetry.Summarize(samples, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(2500, summary.MedianDurationMs);
    }

    [Fact]
    public void RecordCompletion_EvictsOldestSampleBeyondCapacity()
    {
        var telemetry = new AutoReviewQueueTelemetry(capacity: 2);
        telemetry.RecordCompletion(Now.AddMinutes(-3), TimeSpan.FromSeconds(1), drained: true);
        telemetry.RecordCompletion(Now.AddMinutes(-2), TimeSpan.FromSeconds(2), drained: true);
        telemetry.RecordCompletion(Now.AddMinutes(-1), TimeSpan.FromSeconds(3), drained: true);

        var summary = telemetry.Summarize(Now, TimeSpan.FromMinutes(30));

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(2500, summary.MedianDurationMs);
    }

    private static AutoReviewQueueTelemetrySample Sample(DateTime completedAtUtc, double elapsedMs, bool drained)
        => new(completedAtUtc, elapsedMs, drained);
}
