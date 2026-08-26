using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>Architecture pin for the durable one-attempt-per-hour repair loop.</summary>
public sealed class LocalCliSelfHealBreakerTest
{
    [Fact]
    public void RepairAttemptBudget_IsOnePerHourPerCli()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

        Assert.False(LocalCliRepairPolicy.CanAttempt(
            attemptedAt, attemptedAt.AddMinutes(59), LocalCliSelfHealService.RepairCooldown));
        Assert.True(LocalCliRepairPolicy.CanAttempt(
            attemptedAt, attemptedAt.AddHours(1), LocalCliSelfHealService.RepairCooldown));
    }
}
