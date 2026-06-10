
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Matrix lock for <see cref="Watchdog.DecideState"/>. The whole point of
/// keeping the watchdog as a pure function is that the boundary cases
/// (warm-up grace, quiet/suspicious/hung thresholds) sit in one table
/// here instead of scattered through the runner.
/// </summary>
public class WatchdogTests
{
    private static readonly WatchdogConfig Default = WatchdogConfig.Default;

    [Theory]
    // Within warm-up grace -> always healthy, even with long silence.
    [InlineData(  0,   0, WatchdogState.Healthy)]
    [InlineData(120,  20, WatchdogState.Healthy)]
    [InlineData( 29,  29, WatchdogState.Healthy)]
    // Past warm-up, fresh streaming -> healthy.
    [InlineData(  0,  60, WatchdogState.Healthy)]
    [InlineData( 29,  60, WatchdogState.Healthy)]
    // Quiet boundary (30s of silence after warm-up).
    [InlineData( 30,  60, WatchdogState.Quiet)]
    [InlineData( 45,  60, WatchdogState.Quiet)]
    [InlineData( 59,  90, WatchdogState.Quiet)]
    // Suspicious boundary (60s of silence).
    [InlineData( 60,  90, WatchdogState.Suspicious)]
    [InlineData( 90, 100, WatchdogState.Suspicious)]
    [InlineData(119, 130, WatchdogState.Suspicious)]
    // Hung boundary (120s of silence).
    [InlineData(120, 130, WatchdogState.Hung)]
    [InlineData(180, 200, WatchdogState.Hung)]
    public void DecideState_MatrixWithDefaults(double silence, double age, WatchdogState expected)
    {
        Assert.Equal(expected, Watchdog.DecideState(silence, age, Default));
    }

    [Fact]
    public void DecideState_DisabledConfig_AlwaysHealthy()
    {
        var disabled = Default with { Enabled = false };
        Assert.Equal(WatchdogState.Healthy, Watchdog.DecideState(9999, 9999, disabled));
    }

    [Theory]
    [InlineData(WatchdogState.Healthy,    WatchdogState.Quiet,      true)]
    [InlineData(WatchdogState.Quiet,      WatchdogState.Suspicious, true)]
    [InlineData(WatchdogState.Suspicious, WatchdogState.Hung,       true)]
    [InlineData(WatchdogState.Hung,       WatchdogState.Healthy,    true)]
    [InlineData(WatchdogState.Quiet,      WatchdogState.Quiet,      false)]
    [InlineData(WatchdogState.Hung,       WatchdogState.Hung,       false)]
    public void ShouldAnnounce_OnlyOnTransition(WatchdogState prev, WatchdogState next, bool expected)
    {
        Assert.Equal(expected, Watchdog.ShouldAnnounce(prev, next));
    }
}
