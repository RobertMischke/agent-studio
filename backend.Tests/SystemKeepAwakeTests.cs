using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Lifecycle lock for <see cref="SystemKeepAwake"/>: the power request must be
/// acquired on the 0-&gt;active edge, released on the active-&gt;0 edge, kept held
/// (idempotent) while runs stay active, and never touched when disabled. Uses
/// the <see cref="NoopPowerRequest"/> spy so no real OS power state is involved.
/// </summary>
public class SystemKeepAwakeTests
{
    /// <summary>Records every call so the test can assert the reason text too.</summary>
    private sealed class SpyPowerRequest : ISystemPowerRequest
    {
        public int AcquireCount;
        public int ReleaseCount;
        public int UpdateReasonCount;
        public bool Held;
        public string? LastReason;

        public void Acquire(string reason) { AcquireCount++; Held = true; LastReason = reason; }
        public void UpdateReason(string reason) { UpdateReasonCount++; LastReason = reason; }
        public void Release() { if (Held) { ReleaseCount++; Held = false; } }
    }

    [Fact]
    public void Update_FirstActiveRun_AcquiresOnce()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);

        Assert.Equal(1, spy.AcquireCount);
        Assert.True(spy.Held);
        Assert.True(keepAwake.IsHeld);
    }

    [Fact]
    public void Update_SameCountRepeated_IsIdempotent()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);
        keepAwake.Update(1);
        keepAwake.Update(1);

        Assert.Equal(1, spy.AcquireCount);
        Assert.Equal(0, spy.ReleaseCount);
        Assert.Equal(0, spy.UpdateReasonCount);
    }

    [Fact]
    public void Update_CountChangesWhileHeld_RefreshesReasonWithoutReacquire()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);
        keepAwake.Update(2);

        Assert.Equal(1, spy.AcquireCount);
        Assert.Equal(0, spy.ReleaseCount);
        Assert.Equal(1, spy.UpdateReasonCount);
        Assert.Contains("2", spy.LastReason);
        Assert.True(keepAwake.IsHeld);
    }

    [Fact]
    public void Update_BackToZero_Releases()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);
        keepAwake.Update(0);

        Assert.Equal(1, spy.AcquireCount);
        Assert.Equal(1, spy.ReleaseCount);
        Assert.False(spy.Held);
        Assert.False(keepAwake.IsHeld);
    }

    [Fact]
    public void Update_ZeroWhenNotHeld_IsNoOp()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(0);

        Assert.Equal(0, spy.AcquireCount);
        Assert.Equal(0, spy.ReleaseCount);
        Assert.False(keepAwake.IsHeld);
    }

    [Fact]
    public void Update_AcquireReleaseReacquire_FullCycle()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1); // acquire
        keepAwake.Update(0); // release
        keepAwake.Update(3); // re-acquire

        Assert.Equal(2, spy.AcquireCount);
        Assert.Equal(1, spy.ReleaseCount);
        Assert.True(keepAwake.IsHeld);
        Assert.Contains("3", spy.LastReason);
    }

    [Fact]
    public void Update_NegativeCount_TreatedAsZero()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(2);
        keepAwake.Update(-5);

        Assert.Equal(1, spy.ReleaseCount);
        Assert.False(keepAwake.IsHeld);
    }

    [Fact]
    public void Update_WhenDisabled_NeverTouchesRequest()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy, enabled: false);

        keepAwake.Update(5);
        keepAwake.Update(0);

        Assert.Equal(0, spy.AcquireCount);
        Assert.Equal(0, spy.ReleaseCount);
        Assert.False(keepAwake.IsHeld);
    }

    [Fact]
    public void Dispose_WhileHeld_ReleasesRequest()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);
        keepAwake.Dispose();

        Assert.Equal(1, spy.ReleaseCount);
        Assert.False(spy.Held);
    }

    [Fact]
    public void Reason_IsSingularForOneRun_PluralOtherwise()
    {
        var spy = new SpyPowerRequest();
        var keepAwake = new SystemKeepAwake(spy);

        keepAwake.Update(1);
        Assert.Contains("1 aktive Agent-Run", spy.LastReason);
        Assert.DoesNotContain("Runs", spy.LastReason);

        keepAwake.Update(2);
        Assert.Contains("2 aktive Agent-Runs", spy.LastReason);
    }
}
