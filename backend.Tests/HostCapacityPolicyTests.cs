using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the host-capacity admission contract (AGT-2302 / AGT-2376): the
/// ceiling is a hard stop, the ramp bounds how fast concurrency grows, the
/// target load stops growth on a hot host, and a host without central targets
/// still inherits the deprecated per-project value during migration.
/// </summary>
public class HostCapacityPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static HostCapacityTargets Targets(
        int max = 4,
        int targetLoad = 80,
        string ramp = RunnerRampStrategies.Balanced)
        => new(max, targetLoad, ramp);

    [Fact]
    public void Ceiling_HoldsFurtherClaims_WhenEveryLeaseIsSpent()
    {
        var verdict = HostCapacityPolicy.Decide(
            Targets(max: 4),
            new HostAdmissionFacts(ActiveRuns: 4, Now));

        Assert.False(verdict.Admitted);
        Assert.Equal(HostAdmissionReasons.CeilingReached, verdict.ReasonCode);
        Assert.Contains("4/4", verdict.Detail);
    }

    [Fact]
    public void Ceiling_AdmitsWhileSlotsRemain()
    {
        var verdict = HostCapacityPolicy.Decide(
            Targets(max: 4),
            new HostAdmissionFacts(ActiveRuns: 3, Now, LastAdmissionAt: Now.AddHours(-1)));

        Assert.True(verdict.Admitted);
        Assert.Equal(HostAdmissionReasons.Admitted, verdict.ReasonCode);
    }

    [Fact]
    public void Ceiling_IsNeverExceeded_ByAnOverReportedActiveCount()
    {
        var verdict = HostCapacityPolicy.Decide(
            Targets(max: 2),
            new HostAdmissionFacts(ActiveRuns: 7, Now));

        Assert.False(verdict.Admitted);
        Assert.Equal(0, HostCapacityPolicy.FreeSlots(2, 7));
    }

    [Fact]
    public void Ramp_LimitsHowFastConcurrencyGrows()
    {
        var conservative = Targets(ramp: RunnerRampStrategies.Conservative);

        var tooSoon = HostCapacityPolicy.Decide(
            conservative,
            new HostAdmissionFacts(ActiveRuns: 1, Now, LastAdmissionAt: Now.AddSeconds(-30)));
        Assert.False(tooSoon.Admitted);
        Assert.Equal(HostAdmissionReasons.RampLimited, tooSoon.ReasonCode);

        var afterInterval = HostCapacityPolicy.Decide(
            conservative,
            new HostAdmissionFacts(ActiveRuns: 1, Now, LastAdmissionAt: Now.AddSeconds(-61)));
        Assert.True(afterInterval.Admitted);
    }

    [Fact]
    public void Ramp_NeverStallsAnIdleHost()
    {
        var verdict = HostCapacityPolicy.Decide(
            Targets(ramp: RunnerRampStrategies.Conservative),
            new HostAdmissionFacts(ActiveRuns: 0, Now, LastAdmissionAt: Now.AddSeconds(-1)));

        Assert.True(verdict.Admitted);
    }

    [Fact]
    public void Ramp_BalancedIsTheUnchangedDefault_AndFillsTheCeilingBackToBack()
    {
        // Adopting host capacity must not silently slow an existing fleet: only
        // the conservative strategy paces admissions.
        var verdict = HostCapacityPolicy.Decide(
            Targets(ramp: RunnerRampStrategies.Balanced),
            new HostAdmissionFacts(ActiveRuns: 1, Now, LastAdmissionAt: Now));

        Assert.True(verdict.Admitted);
        Assert.Equal(TimeSpan.Zero, HostCapacityPolicy.RampInterval(RunnerRampStrategies.Balanced));
        Assert.Equal(TimeSpan.Zero, HostCapacityPolicy.RampInterval("something-unknown"));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            HostCapacityPolicy.RampInterval(RunnerRampStrategies.Conservative));
    }

    [Fact]
    public void TargetLoad_StopsGrowthOnAHotHost_ButNotTheFirstRun()
    {
        var hot = HostCapacityPolicy.Decide(
            Targets(targetLoad: 80),
            new HostAdmissionFacts(ActiveRuns: 1, Now, CpuPercent: 93));
        Assert.False(hot.Admitted);
        Assert.Equal(HostAdmissionReasons.TargetLoadExceeded, hot.ReasonCode);

        var idleButHot = HostCapacityPolicy.Decide(
            Targets(targetLoad: 80),
            new HostAdmissionFacts(ActiveRuns: 0, Now, CpuPercent: 93));
        Assert.True(idleButHot.Admitted);
    }

    [Fact]
    public void TargetLoad_IsIgnoredByAnAggressiveHost_ButTheCeilingIsNot()
    {
        var aggressive = Targets(max: 4, targetLoad: 80, ramp: RunnerRampStrategies.Aggressive);

        Assert.True(HostCapacityPolicy
            .Decide(aggressive, new HostAdmissionFacts(ActiveRuns: 1, Now, CpuPercent: 99))
            .Admitted);
        Assert.False(HostCapacityPolicy
            .Decide(aggressive, new HostAdmissionFacts(ActiveRuns: 4, Now, CpuPercent: 5))
            .Admitted);
    }

    [Fact]
    public void ResolveCeiling_PrefersTheCentralHostTarget()
    {
        Assert.Equal(
            12,
            HostCapacityPolicy.ResolveCeiling(hostCeiling: 12, projectCompatCeiling: 3, bootstrapCeiling: 2));
    }

    [Fact]
    public void ResolveCeiling_SeedsFromTheDaemon_AndOnlyNarrowsItWithTheProjectValue()
    {
        // Compat path: no central target yet. The daemon's own parallelism is
        // the seed; the deprecated project value may narrow it (an operator
        // intent to run fewer things at once) but may never raise it - a project
        // cap of 6 on a daemon that runs 2 would hand out three times the slots
        // the host actually has.
        Assert.Equal(
            2,
            HostCapacityPolicy.ResolveCeiling(hostCeiling: null, projectCompatCeiling: 6, bootstrapCeiling: 2));
        Assert.Equal(
            3,
            HostCapacityPolicy.ResolveCeiling(hostCeiling: null, projectCompatCeiling: 3, bootstrapCeiling: 8));
        // A sequential project default (1) carries no opinion at all.
        Assert.Equal(
            4,
            HostCapacityPolicy.ResolveCeiling(hostCeiling: null, projectCompatCeiling: 1, bootstrapCeiling: 4));
    }

    [Fact]
    public void ResolveCeiling_StaysUnknown_WhenNobodyDeclaredACapacity()
    {
        // A sequential project default (1) carries no opinion, and inventing a
        // ceiling here would silently throttle a fleet that never asked for one.
        Assert.Null(HostCapacityPolicy.ResolveCeiling(null, null, null));
        Assert.Null(HostCapacityPolicy.ResolveCeiling(null, projectCompatCeiling: 1, bootstrapCeiling: null));
    }

    [Fact]
    public void ResolveCeiling_EnforcesNothing_WhenOnlyAProjectValueExists()
    {
        // The host declared no capacity at all (an old daemon that sends neither
        // its bootstrap value nor an adopted one). A project setting alone must
        // not become a server-enforced host cap: the server enforces nothing
        // until the host or an operator declares something.
        Assert.Null(HostCapacityPolicy.ResolveCeiling(
            hostCeiling: null, projectCompatCeiling: 6, bootstrapCeiling: null));
        Assert.Null(HostCapacityPolicy.ResolveCeiling(
            hostCeiling: null, projectCompatCeiling: 6, bootstrapCeiling: 0));
    }

    [Fact]
    public void ResolveCeiling_ClampsOutOfRangeValues()
    {
        Assert.Equal(256, HostCapacityPolicy.ResolveCeiling(9999, null, null));
        Assert.Equal(1, HostCapacityPolicy.ClampCeiling(0));
        Assert.Equal(50, HostCapacityPolicy.ClampTargetLoad(10));
        Assert.Equal(95, HostCapacityPolicy.ClampTargetLoad(400));
    }

    [Theory]
    [InlineData("Conservative", RunnerRampStrategies.Conservative)]
    [InlineData(" aggressive ", RunnerRampStrategies.Aggressive)]
    [InlineData("nonsense", RunnerRampStrategies.Balanced)]
    [InlineData(null, RunnerRampStrategies.Balanced)]
    public void RampStrategy_NormalisesToAKnownValue(string? input, string expected)
        => Assert.Equal(expected, RunnerRampStrategies.Normalize(input));
}
