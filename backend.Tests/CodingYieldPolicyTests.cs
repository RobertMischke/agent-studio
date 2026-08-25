using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingYieldPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CodingYieldOptions Options = CodingYieldOptions.Default;

    [Theory]
    // reviewDepth, current, lastChangeMinutesAgo, expectedAction, expectedTarget
    [InlineData(10, 4, null, CodingYieldAction.Yield, 3)]
    [InlineData(9, 4, null, CodingYieldAction.Hold, 4)]
    [InlineData(10, 1, null, CodingYieldAction.Hold, 1)]
    [InlineData(10, 4, 2d, CodingYieldAction.Hold, 4)]
    [InlineData(10, 4, 5d, CodingYieldAction.Yield, 3)]
    public void Evaluate_YieldsOneStepAtATimeWithinCooldownAndFloor(
        int reviewDepth,
        int current,
        double? lastChangeMinutesAgo,
        CodingYieldAction expectedAction,
        int expectedTarget)
    {
        var lastChangeAt = lastChangeMinutesAgo is { } minutes ? Now.AddMinutes(-minutes) : (DateTime?)null;

        var decision = CodingYieldPolicy.Evaluate(
            current, reviewDepth, reviewIsStagnant: false, Now, lastChangeAt,
            restoreEligibleSinceUtc: null, Options);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedTarget, decision.RecommendedCodingParallelism);
    }

    [Fact]
    public void Evaluate_StagnationYieldsEvenBelowTheDepthThreshold()
    {
        var decision = CodingYieldPolicy.Evaluate(
            currentCodingParallelism: 4,
            reviewQueueDepth: 3,
            reviewIsStagnant: true,
            Now,
            lastChangeAtUtc: null,
            restoreEligibleSinceUtc: null,
            Options);

        Assert.Equal(CodingYieldAction.Yield, decision.Action);
        Assert.Equal(3, decision.RecommendedCodingParallelism);
        Assert.Contains("stagnant", decision.Reason);
    }

    [Theory]
    // reviewDepth, eligibleSinceMinutesAgo, lastChangeMinutesAgo, expectedAction, expectedTarget
    [InlineData(2, 10d, 20d, CodingYieldAction.Restore, 3)]
    [InlineData(2, 3d, 20d, CodingYieldAction.Hold, 2)]
    [InlineData(2, 10d, 5d, CodingYieldAction.Hold, 2)]
    [InlineData(5, 10d, 20d, CodingYieldAction.Hold, 2)]
    public void Evaluate_RestoresOnlyAfterDepthStaysInTheRestoreBandLongEnough(
        int reviewDepth,
        double eligibleSinceMinutesAgo,
        double lastChangeMinutesAgo,
        CodingYieldAction expectedAction,
        int expectedTarget)
    {
        var decision = CodingYieldPolicy.Evaluate(
            currentCodingParallelism: 2,
            reviewQueueDepth: reviewDepth,
            reviewIsStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-lastChangeMinutesAgo),
            restoreEligibleSinceUtc: Now.AddMinutes(-eligibleSinceMinutesAgo),
            Options);

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedTarget, decision.RecommendedCodingParallelism);
    }

    [Fact]
    public void Evaluate_NeverRestoresAboveTheConfiguredBaseline()
    {
        var decision = CodingYieldPolicy.Evaluate(
            currentCodingParallelism: Options.CodingBaselineParallelism,
            reviewQueueDepth: 0,
            reviewIsStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-30),
            restoreEligibleSinceUtc: Now.AddMinutes(-30),
            Options);

        Assert.Equal(CodingYieldAction.Hold, decision.Action);
        Assert.Equal(Options.CodingBaselineParallelism, decision.RecommendedCodingParallelism);
    }

    [Fact]
    public void Evaluate_HysteresisGapBetweenYieldAndRestoreThresholdsPreventsFlapping()
    {
        // A depth sitting between the restore and yield thresholds must hold in
        // either direction - this is the dead zone that stops oscillation at a
        // single boundary value from ratcheting coding capacity back and forth.
        var deadZoneDepth = (Options.RestoreQueueDepthThreshold + Options.YieldQueueDepthThreshold) / 2;

        var decision = CodingYieldPolicy.Evaluate(
            currentCodingParallelism: 2,
            reviewQueueDepth: deadZoneDepth,
            reviewIsStagnant: false,
            Now,
            lastChangeAtUtc: Now.AddMinutes(-30),
            restoreEligibleSinceUtc: null,
            Options);

        Assert.Equal(CodingYieldAction.Hold, decision.Action);
    }
}
