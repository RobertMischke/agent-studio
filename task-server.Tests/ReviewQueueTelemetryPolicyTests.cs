using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class ReviewQueueTelemetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Calculates_hourly_drain_rate_and_even_sample_median()
    {
        var snapshot = ReviewQueueTelemetryPolicy.Evaluate(
            Now,
            queueDepth: 8,
            waitingDepth: 4,
            activeReviews: 4,
            oldestWaitingAt: Now.AddMinutes(-10),
            lastDrainAt: Now.AddMinutes(-5),
            completions:
            [
                new ReviewCompletionSampleDto(Now.AddMinutes(-50), 600),
                new ReviewCompletionSampleDto(Now.AddMinutes(-30), 1_200),
                new ReviewCompletionSampleDto(Now.AddHours(-2), 1_800),
                new ReviewCompletionSampleDto(Now.AddHours(-3), 2_400),
            ],
            drainWindow: TimeSpan.FromHours(1),
            durationWindow: TimeSpan.FromHours(24),
            stagnationThreshold: TimeSpan.FromMinutes(30));

        Assert.Equal(2, snapshot.DrainRatePerHour);
        Assert.Equal(1_500, snapshot.MedianReviewDurationSeconds);
        Assert.Equal(4, snapshot.DurationSampleCount);
        Assert.False(snapshot.Stagnant);
    }

    [Theory]
    [InlineData(3, -31, null, true, 31)]
    [InlineData(3, -31, -5, false, 0)]
    [InlineData(0, -60, null, false, 0)]
    public void Stagnation_requires_waiting_work_without_recent_drain_progress(
        int waitingDepth,
        int oldestMinutes,
        int? lastDrainMinutes,
        bool expected,
        int stagnantForMinutes)
    {
        var snapshot = ReviewQueueTelemetryPolicy.Evaluate(
            Now,
            queueDepth: 4,
            waitingDepth,
            activeReviews: 0,
            oldestWaitingAt: Now.AddMinutes(oldestMinutes),
            lastDrainAt: lastDrainMinutes is { } value ? Now.AddMinutes(value) : null,
            completions: [],
            drainWindow: TimeSpan.FromHours(1),
            durationWindow: TimeSpan.FromHours(24),
            stagnationThreshold: TimeSpan.FromMinutes(30));

        Assert.Equal(expected, snapshot.Stagnant);
        Assert.Equal(stagnantForMinutes, snapshot.StagnantForMinutes);
    }
}
