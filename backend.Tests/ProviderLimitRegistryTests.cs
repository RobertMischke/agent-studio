using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderLimitRegistryTests
{
    [Fact]
    public void Detect_ClaudeSessionLimit_ParsesNextLocalReset()
    {
        var now = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);

        var signal = ProviderLimitDetector.Detect(
            "claude",
            ["You've hit your session limit (resets 12:20am)"],
            now,
            TimeZoneInfo.Utc);

        Assert.NotNull(signal);
        Assert.True(signal.ResetTimeReported);
        Assert.Equal(new DateTime(2026, 8, 24, 0, 20, 0, DateTimeKind.Utc), signal.LimitedUntil);
        Assert.Contains("claude: limited until", signal.Reason);
    }

    [Fact]
    public void Detect_ExhaustionWithoutReset_SchedulesBoundedProbe()
    {
        var now = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);

        var signal = ProviderLimitDetector.Detect(
            "claude",
            ["Error: rate_limit_exceeded"],
            now,
            TimeZoneInfo.Utc);

        Assert.NotNull(signal);
        Assert.False(signal.ResetTimeReported);
        Assert.Equal(now + ProviderLimitDetector.UnknownResetRetry, signal.LimitedUntil);
    }

    [Fact]
    public void Detect_RejectedTelemetry_ParsesEpochReset()
    {
        var now = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
        var reset = now.AddHours(2);

        var signal = ProviderLimitDetector.Detect(
            "claude",
            [$"Rate limit · rejected [status=rejected resetsAt={new DateTimeOffset(reset).ToUnixTimeSeconds()}]"],
            now,
            TimeZoneInfo.Utc);

        Assert.NotNull(signal);
        Assert.True(signal.ResetTimeReported);
        Assert.Equal(reset, signal.LimitedUntil);
    }

    [Fact]
    public void Detect_BenignAllowedTelemetry_DoesNotPauseProvider()
    {
        var signal = ProviderLimitDetector.Detect(
            "claude",
            ["Rate limit · five-hour · allowed status=allowed"],
            DateTime.UtcNow,
            TimeZoneInfo.Utc);

        Assert.Null(signal);
    }

    [Fact]
    public void Registry_PausesOnlyAffectedCli_ThenClearsAtReset()
    {
        var registry = new ProviderLimitRegistry();
        var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
        var reset = observed.AddHours(2);
        registry.Record(new ProviderLimitStatus(
            "claude", observed, reset, "claude: limited until reset", true));

        Assert.NotNull(registry.GetActive("claude", observed.AddMinutes(1)));
        Assert.Null(registry.GetActive("codex", observed.AddMinutes(1)));
        Assert.Empty(registry.Active(reset));
        Assert.Null(registry.GetActive("claude", reset));
    }
}
