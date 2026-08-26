using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionMonitorHostedServiceTests
{
    [Theory]
    [InlineData(null, "codex-cli 0.149.0", false)]
    [InlineData("codex-cli 0.149.0", "codex-cli 0.149.0", false)]
    [InlineData("codex-cli 0.144.1", "codex-cli 0.149.0", true)]
    [InlineData("2.1.201 (Claude Code)", "2.1.202 (Claude Code)", true)]
    public void HasChanged_DistinguishesFirstObservationFromVersionDrift(
        string? previous,
        string current,
        bool expected)
    {
        Assert.Equal(expected, CliVersionMonitorHostedService.HasChanged(previous, current));
    }
}
