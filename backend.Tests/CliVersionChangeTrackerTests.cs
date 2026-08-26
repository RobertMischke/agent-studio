using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionChangeTrackerTests
{
    [Fact]
    public void Observe_ReportsOnlyAnActualVersionChange()
    {
        var tracker = new CliVersionChangeTracker();

        Assert.Null(tracker.Observe("codex", "codex-cli 0.144.1"));
        Assert.Null(tracker.Observe("codex", "codex-cli 0.144.1"));

        var change = tracker.Observe("codex", "codex-cli 0.149.0");

        Assert.NotNull(change);
        Assert.Equal("codex-cli 0.144.1", change.PreviousVersion);
        Assert.Equal("codex-cli 0.149.0", change.CurrentVersion);
        Assert.Null(tracker.Observe("codex", "codex-cli 0.149.0"));
    }

    [Fact]
    public void Seed_MakesARestartChangeAttributable()
    {
        var tracker = new CliVersionChangeTracker();
        tracker.Seed("claude", "2.1.202 (Claude Code)");

        var change = tracker.Observe("claude", "2.1.204 (Claude Code)");

        Assert.NotNull(change);
        Assert.Equal("2.1.202 (Claude Code)", change.PreviousVersion);
        Assert.Equal("2.1.204 (Claude Code)", change.CurrentVersion);
    }
}
