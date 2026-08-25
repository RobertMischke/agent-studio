using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderLimitRegistryTests
{
    [Fact]
    public void Provider_limit_expires_without_manual_intervention()
    {
        var registry = new ProviderLimitRegistry();
        var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
        registry.Record(new ProviderLimitDetection(
            "claude", observed, observed.AddHours(2), "session limit"));

        Assert.NotNull(registry.Current("claude", observed.AddMinutes(30)));
        Assert.Null(registry.Current("claude", observed.AddHours(2)));
    }

    [Fact]
    public void Claude_limit_does_not_limit_codex()
    {
        var registry = new ProviderLimitRegistry();
        var observed = DateTime.UtcNow;
        registry.Record(new ProviderLimitDetection(
            "claude", observed, observed.AddHours(2), "session limit"));

        Assert.NotNull(registry.Current("claude", observed));
        Assert.Null(registry.Current("codex", observed));
    }
}
