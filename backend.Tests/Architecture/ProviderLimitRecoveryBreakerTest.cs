using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>Architecture lock for provider-limit.recovery-probe.</summary>
public sealed class ProviderLimitRecoveryBreakerTest
{
    [Fact]
    public void Recovery_tick_is_bounded_and_manual_projects_do_not_auto_resume()
    {
        Assert.Equal(15, ProviderQuotaWaitPolicy.DefaultIntervalSeconds);
        Assert.Equal(100, ProviderQuotaWaitPolicy.MaxCardsPerTick);

        var resetAt = new DateTime(2026, 8, 24, 0, 20, 0, DateTimeKind.Utc);
        var wait = new QuotaWaitStatus(
            CliTypes.Claude,
            resetAt.AddHours(-2),
            resetAt,
            120,
            "claude: limited until reset",
            "provider-limit");
        var recovered = new[]
        {
            new ProviderCapabilityAvailability(CliTypes.Claude, "ready", true, "healthy"),
        };

        Assert.False(ProviderQuotaWaitPolicy.CanResume(wait, resetAt, false, recovered));
        Assert.True(ProviderQuotaWaitPolicy.CanResume(wait, resetAt, true, recovered));
    }
}
