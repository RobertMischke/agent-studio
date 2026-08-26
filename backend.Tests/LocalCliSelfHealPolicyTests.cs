using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealPolicyTests
{
    [Theory]
    [InlineData(true, true, true, NpmCliInstallDisposition.Available)]
    [InlineData(false, true, false, NpmCliInstallDisposition.MissingShimWithPackagePresent)]
    [InlineData(false, false, false, NpmCliInstallDisposition.TrulyUninstalled)]
    [InlineData(false, true, true, NpmCliInstallDisposition.BrokenExecutable)]
    public void Classification_distinguishes_missing_shim_from_uninstalled_package(
        bool executableAvailable,
        bool packagePresent,
        bool launchShimPresent,
        NpmCliInstallDisposition expected)
    {
        var actual = NpmCliInstallPolicy.Classify(new NpmCliInstallSnapshot(
            executableAvailable,
            packagePresent,
            launchShimPresent));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Repair_attempt_bound_is_one_hour()
        => Assert.Equal(TimeSpan.FromHours(1), LocalCliSelfHealService.RepairCooldown);

    [Theory]
    [InlineData("claude", "@anthropic-ai/claude-code")]
    [InlineData("codex", "@openai/codex")]
    [InlineData("gemini", null)]
    public void Only_supported_local_npm_clis_have_repair_packages(string cliType, string? expected)
        => Assert.Equal(expected, LocalCliSelfHealService.PackageFor(cliType));
}
