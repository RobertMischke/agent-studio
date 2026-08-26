using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealBreakerTests
{
    [Fact]
    public void Repair_attempt_is_refused_until_one_hour_has_elapsed()
    {
        var attemptedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(LocalCliSelfHealService.MayAttempt(
            attemptedAt,
            attemptedAt.Add(LocalCliSelfHealService.MinimumAttemptInterval).AddTicks(-1)));
        Assert.True(LocalCliSelfHealService.MayAttempt(
            attemptedAt,
            attemptedAt.Add(LocalCliSelfHealService.MinimumAttemptInterval)));
    }
}
