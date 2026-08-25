using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderLimitBehaviorTests
{
    [Fact]
    public void Claude_session_limit_extracts_the_next_reset_time()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("incident", TimeSpan.FromHours(2), "incident", "incident");
        var detected = ProviderLimitParser.Detect(
            "claude",
            "You've hit your session limit · resets 12:20am",
            new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc),
            zone);

        Assert.NotNull(detected);
        Assert.Equal(new DateTime(2026, 8, 23, 22, 20, 0, DateTimeKind.Utc), detected!.RetryAt);
        Assert.Equal("12:20am", detected.ReportedReset);
    }

    [Fact]
    public void Limit_is_provider_specific_and_advertised_until_reset()
    {
        var now = new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);
        var probe = new ProviderAuthProbe(
            executableExists: _ => true,
            clock: () => now);
        probe.MarkProviderLimited(new ProviderLimitDetection(
            "claude", now.UtcDateTime, now.AddHours(2).UtcDateTime, "session limit"));

        var claude = probe.Current("claude");
        var codex = probe.Current("codex");

        Assert.Equal(ProviderAuthProbe.Unavailable, claude.Status);
        Assert.Contains("claude: limited until", claude.Detail);
        Assert.Equal(ProviderAuthProbe.Ready, codex.Status);
    }
}
