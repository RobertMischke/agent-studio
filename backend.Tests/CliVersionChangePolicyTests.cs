using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionChangePolicyTests
{
    [Theory]
    [InlineData(true, "codex-cli 0.149.0", "/usr/bin/codex", true, "codex-cli 0.149.0", "/usr/bin/codex", false)]
    [InlineData(true, "codex-cli 0.144.1", "/usr/bin/codex", true, "codex-cli 0.149.0", "/usr/bin/codex", true)]
    [InlineData(true, "2.1.202 (Claude Code)", "/usr/bin/claude", false, null, "/usr/bin/claude", true)]
    [InlineData(true, "codex-cli 0.149.0", "/usr/bin/codex", true, "codex-cli 0.149.0", "/opt/codex", true)]
    public void Changed_ReportsVersionAvailabilityAndPathDrift(
        bool previousAvailable,
        string? previousVersion,
        string previousPath,
        bool currentAvailable,
        string? currentVersion,
        string currentPath,
        bool expected)
    {
        var previous = new CliVersionObservation(previousAvailable, previousVersion, previousPath);
        var current = new CliVersionObservation(currentAvailable, currentVersion, currentPath);

        Assert.Equal(expected, CliVersionChangePolicy.Changed(previous, current));
    }
}
