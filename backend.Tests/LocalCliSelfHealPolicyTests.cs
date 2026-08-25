using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealPolicyTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai/claude-code")]
    [InlineData("codex", "@openai/codex")]
    public void Missing_shims_with_the_global_package_present_are_repairable(
        string cliType,
        string expectedPackage)
    {
        var npmBin = Path.Combine("C:", "Users", "operator", "AppData", "Roaming", "npm");
        var packageDirectory = Path.Combine(
            [npmBin, "node_modules", .. expectedPackage.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

        var inspection = NpmShimRepairPolicy.Inspect(
            cliType,
            cliType,
            npmBin,
            _ => false,
            path => path == packageDirectory);

        Assert.Equal(NpmShimInstallState.MissingShimWithPackagePresent, inspection.State);
        Assert.Equal(expectedPackage, inspection.PackageName);
        Assert.Equal(packageDirectory, inspection.PackageDirectory);
    }

    [Fact]
    public void Absent_package_is_truly_uninstalled_and_never_auto_installed()
    {
        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Claude,
            "claude",
            "npm-bin",
            _ => false,
            _ => false);

        Assert.Equal(NpmShimInstallState.TrulyUninstalled, inspection.State);
    }

    [Fact]
    public void Existing_cmd_shim_is_not_classified_as_broken()
    {
        var npmBin = "npm-bin";
        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Codex,
            "codex",
            npmBin,
            path => path == Path.Combine(npmBin, "codex.cmd"),
            _ => true);

        Assert.Equal(NpmShimInstallState.Available, inspection.State);
    }

    [Fact]
    public void Leftover_powershell_shim_does_not_hide_a_missing_cmd_shim()
    {
        var npmBin = "npm-bin";
        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Claude,
            "claude",
            npmBin,
            path => path == Path.Combine(npmBin, "claude.ps1"),
            _ => true);

        Assert.Equal(NpmShimInstallState.MissingShimWithPackagePresent, inspection.State);
    }

    [Fact]
    public void Explicit_custom_binary_path_is_outside_global_shim_repair_scope()
    {
        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Claude,
            Path.Combine("D:", "tools", "claude.exe"),
            "npm-bin",
            _ => false,
            _ => true);

        Assert.Equal(NpmShimInstallState.Unsupported, inspection.State);
    }

    [Fact]
    public void Repair_attempt_is_bounded_to_one_per_hour()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        Assert.True(NpmShimRepairPolicy.CanAttempt(null, now));
        Assert.False(NpmShimRepairPolicy.CanAttempt(now.AddMinutes(-59), now));
        Assert.True(NpmShimRepairPolicy.CanAttempt(now.AddHours(-1), now));
    }
}
