using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionChangeTrackerTests
{
    [Fact]
    public void Observe_LogsRestartDriftAgainstPersistedQuotaVersion()
    {
        var tracker = new CliVersionChangeTracker();

        var observation = tracker.Observe(
            "codex",
            "codex-cli 0.149.0",
            "codex-cli 0.148.0");

        Assert.True(observation.FirstObservation);
        Assert.True(observation.Changed);
        Assert.Equal("codex-cli 0.148.0", observation.PreviousVersion);
        Assert.Equal("codex-cli 0.149.0", observation.CurrentVersion);
    }

    [Fact]
    public void Observe_DetectsPeriodicInProcessVersionChangeOnlyOnce()
    {
        var tracker = new CliVersionChangeTracker();

        var startup = tracker.Observe("claude", "2.1.202");
        var changed = tracker.Observe("claude", "2.1.203");
        var unchanged = tracker.Observe("claude", "2.1.203");

        Assert.True(startup.FirstObservation);
        Assert.False(startup.Changed);
        Assert.False(changed.FirstObservation);
        Assert.True(changed.Changed);
        Assert.Equal("2.1.202", changed.PreviousVersion);
        Assert.False(unchanged.Changed);
    }
}
