using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionIdentityTests
{
    [Theory]
    [InlineData("codex-cli 0.149.0", "0.149.0")]
    [InlineData("2.1.202 (Claude Code)", "2.1.202")]
    [InlineData("codex-cli 0.150.0-beta.2", "0.150.0-beta.2")]
    [InlineData("vendor-development-build", "vendor-development-build")]
    [InlineData(null, null)]
    public void Normalize_ExtractsAttributableVersion(string? raw, string? expected)
        => Assert.Equal(expected, CliVersionIdentity.Normalize(raw));

    [Theory]
    [InlineData(false, null, null, CliVersionObservation.Unavailable)]
    [InlineData(true, "0.149.0", null, CliVersionObservation.Unavailable)]
    [InlineData(true, null, "0.149.0", CliVersionObservation.FirstSeen)]
    [InlineData(true, "0.149.0", "0.149.0", CliVersionObservation.Unchanged)]
    [InlineData(true, "0.149.0", "0.149.0-BETA", CliVersionObservation.Changed)]
    [InlineData(true, "0.149.0-beta", "0.149.0-BETA", CliVersionObservation.Unchanged)]
    public void Classify_CoversVersionChangePolicy(
        bool available,
        string? previous,
        string? current,
        CliVersionObservation expected)
        => Assert.Equal(expected, CliVersionIdentity.Classify(available, previous, current));
}
