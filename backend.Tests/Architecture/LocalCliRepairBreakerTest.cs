using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>Pins the persisted one-attempt-per-hour local CLI repair budget.</summary>
public sealed class LocalCliRepairBreakerTest
{
    [Fact]
    public void Missing_shim_repair_budget_is_exactly_one_hour()
    {
        Assert.Equal(TimeSpan.FromHours(1), LocalCliRepairService.AttemptWindow);
        var attemptedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        Assert.False(LocalCliRepairService.AttemptAllowed(
            attemptedAt.Add(LocalCliRepairService.AttemptWindow).AddTicks(-1),
            attemptedAt));
        Assert.True(LocalCliRepairService.AttemptAllowed(
            attemptedAt.Add(LocalCliRepairService.AttemptWindow),
            attemptedAt));
    }
}
