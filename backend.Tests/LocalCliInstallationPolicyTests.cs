using AgentStudio.Cli;

using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliInstallationPolicyTests
{
    [Theory]
    [InlineData(true, true, true, LocalCliInstallationState.Available)]
    [InlineData(false, true, false, LocalCliInstallationState.MissingShimWithPackagePresent)]
    [InlineData(false, false, false, LocalCliInstallationState.Uninstalled)]
    [InlineData(false, true, true, LocalCliInstallationState.BrokenInstall)]
    public void Classify_distinguishes_missing_shim_from_uninstalled_and_other_failures(
        bool cliAvailable,
        bool packagePresent,
        bool callableShimPresent,
        LocalCliInstallationState expected)
    {
        Assert.Equal(expected, LocalCliInstallationPolicy.Classify(
            cliAvailable,
            packagePresent,
            callableShimPresent));
    }

    [Fact]
    public void Repair_budget_allows_only_one_attempt_per_hour()
    {
        var attemptedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(LocalCliInstallationPolicy.MayAttemptRepair(
            attemptedAt,
            attemptedAt.AddMinutes(59),
            LocalCliSelfRepairService.RepairCooldown));
        Assert.True(LocalCliInstallationPolicy.MayAttemptRepair(
            attemptedAt,
            attemptedAt.AddHours(1),
            LocalCliSelfRepairService.RepairCooldown));
    }

    [Fact]
    public void First_repair_attempt_is_allowed()
    {
        Assert.True(LocalCliInstallationPolicy.MayAttemptRepair(
            null,
            DateTime.UtcNow,
            LocalCliSelfRepairService.RepairCooldown));
    }
}
