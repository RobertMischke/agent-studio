using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the tunnel-supervision classification contract (AGT-2664): missing
/// data reads as "not-configured" rather than an error, stale data is called
/// out separately from a genuine failure, and any unregistered/failed
/// component demotes the whole snapshot to "attention".
/// </summary>
public class TunnelSupervisionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static TunnelSupervisionSnapshot Snapshot(
        DateTime? generatedAt = null,
        bool keeperRegistered = true,
        string? keeperStatus = "healthy",
        DateTime? keeperObservedAt = null,
        bool watchdogRegistered = true,
        DateTime? lastProbeAt = null,
        string? lastProbeResult = "ok",
        string? lastHealResult = null)
        => new(
            SchemaVersion: 1,
            GeneratedAt: generatedAt ?? Now,
            Keeper: new TunnelKeeperStatus(
                "AgentRunner-TunnelKeeper", keeperRegistered, "Running", keeperStatus,
                keeperObservedAt ?? Now, "ok"),
            Watchdog: new TunnelWatchdogStatus(
                "AgentRunner-TunnelWatchdog", watchdogRegistered, "Running",
                lastProbeAt ?? Now, lastProbeResult, Now.AddMinutes(-30), lastHealResult, 0));

    [Fact]
    public void MissingSnapshot_IsNotConfigured()
    {
        Assert.Equal(TunnelSupervisionStatuses.NotConfigured, TunnelSupervisionPolicy.Classify(null, Now));
    }

    [Fact]
    public void FreshRegisteredRunningSnapshot_IsHealthy()
    {
        Assert.Equal(TunnelSupervisionStatuses.Healthy, TunnelSupervisionPolicy.Classify(Snapshot(), Now));
    }

    [Fact]
    public void UnregisteredKeeper_IsAttention_EvenWhenFresh()
    {
        var snapshot = Snapshot(keeperRegistered: false);
        Assert.Equal(TunnelSupervisionStatuses.Attention, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void UnregisteredWatchdog_IsAttention_EvenWhenFresh()
    {
        var snapshot = Snapshot(watchdogRegistered: false);
        Assert.Equal(TunnelSupervisionStatuses.Attention, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void KeeperUnreachable_IsAttention()
    {
        var snapshot = Snapshot(keeperStatus: "unreachable");
        Assert.Equal(TunnelSupervisionStatuses.Attention, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void FailedProbe_IsAttention()
    {
        var snapshot = Snapshot(lastProbeResult: "failed");
        Assert.Equal(TunnelSupervisionStatuses.Attention, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void FailedHeal_IsAttention_EvenWhenTheFollowingProbePassed()
    {
        var snapshot = Snapshot(lastProbeResult: "ok", lastHealResult: "failed");
        Assert.Equal(TunnelSupervisionStatuses.Attention, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void SuccessfulHeal_DoesNotOverrideHealthy()
    {
        var snapshot = Snapshot(lastProbeResult: "ok", lastHealResult: "succeeded");
        Assert.Equal(TunnelSupervisionStatuses.Healthy, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void NoRecentActivity_IsStale_RegardlessOfLastKnownGoodStatus()
    {
        var stale = Now.AddMinutes(-20);
        var snapshot = Snapshot(generatedAt: stale, keeperObservedAt: stale, lastProbeAt: stale);
        Assert.Equal(TunnelSupervisionStatuses.Stale, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void JustUnderTheStaleThreshold_IsStillClassifiedNormally()
    {
        var almostStale = Now.AddMinutes(-14);
        var snapshot = Snapshot(generatedAt: almostStale, keeperObservedAt: almostStale, lastProbeAt: almostStale);
        Assert.Equal(TunnelSupervisionStatuses.Healthy, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }

    [Fact]
    public void FreshestComponentTimestamp_WinsOverAnOlderGeneratedAt()
    {
        // The watchdog probed one minute ago even though the combined file was
        // last regenerated 20 minutes ago (an operator ran -StatusOnly less
        // often than the watchdog's own probe loop). That is not stale.
        var snapshot = Snapshot(
            generatedAt: Now.AddMinutes(-20),
            keeperObservedAt: Now.AddMinutes(-20),
            lastProbeAt: Now.AddMinutes(-1));
        Assert.Equal(TunnelSupervisionStatuses.Healthy, TunnelSupervisionPolicy.Classify(snapshot, Now));
    }
}
