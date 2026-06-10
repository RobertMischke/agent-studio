
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the cumulative-duration arithmetic for the CORE "Agent execution"
/// pipeline step.
///
/// The bug this guards against (Symptom 2 of bug-agent-run-metriken): a
/// multi-attempt task spawns the agent several times, but
/// <c>ProjectRunner.RecordCoreRunFinish</c> wrote only the LAST run's duration
/// onto the single persistent CORE step, so the Overview pipeline row showed
/// ~55s for a task that actually ran 5 times. The Overview's separate "Total
/// Duration" surface sums every run, so the two disagreed. This accumulator is
/// the pure core of the fix: each run's finish adds its own duration onto the
/// duration carried forward from prior runs, so the CORE row reflects all
/// attempts consistently with Total Duration.
/// </summary>
public class CoreRunStepAccumulatorTests
{
    private static readonly DateTime Start = new(2026, 6, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RunDurationMs_UsesReportedSeconds_WhenPositive()
    {
        // The CLI reported a concrete duration; trust it over wall-clock.
        Assert.Equal(55_000, CoreRunStepAccumulator.RunDurationMs(55.0, Start, Start.AddSeconds(99)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void RunDurationMs_FallsBackToWallClock_WhenSecondsMissingOrNonPositive(double? seconds)
    {
        // No usable reported duration: fall back to now - startedAt.
        var now = Start.AddSeconds(12);
        Assert.Equal(12_000, CoreRunStepAccumulator.RunDurationMs(seconds, Start, now));
    }

    [Fact]
    public void RunDurationMs_NeverNegative_WhenClockSkewsBackwards()
    {
        Assert.Equal(0, CoreRunStepAccumulator.RunDurationMs(null, Start, Start.AddSeconds(-5)));
    }

    [Fact]
    public void Accumulate_FirstRun_IsJustThisRun()
    {
        // prior = 0 (fresh CORE step): single-run behaviour is unchanged.
        Assert.Equal(55_000, CoreRunStepAccumulator.Accumulate(priorAccumulatedMs: 0, thisRunMs: 55_000));
    }

    [Fact]
    public void Accumulate_SumsAcrossAttempts()
    {
        // Three runs of 55s, 40s, 30s land cumulatively, matching Total Duration.
        var afterRun1 = CoreRunStepAccumulator.Accumulate(0, 55_000);
        var afterRun2 = CoreRunStepAccumulator.Accumulate(afterRun1, 40_000);
        var afterRun3 = CoreRunStepAccumulator.Accumulate(afterRun2, 30_000);

        Assert.Equal(55_000, afterRun1);
        Assert.Equal(95_000, afterRun2);
        Assert.Equal(125_000, afterRun3);
    }

    [Theory]
    [InlineData(-100, 5_000, 5_000)]
    [InlineData(5_000, -100, 5_000)]
    public void Accumulate_ClampsNegativeInputsToZero(long prior, long thisRun, long expected)
    {
        Assert.Equal(expected, CoreRunStepAccumulator.Accumulate(prior, thisRun));
    }
}
