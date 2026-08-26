using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliRepairPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 14, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Missing_shim_with_package_present_requests_reinstall(string cliType)
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            cliType,
            ExecutableAvailable: false,
            PackageDirectoryPresent: true,
            ShimPresent: false,
            ObservedAt: Now,
            LastAttemptAt: null));

        Assert.Equal(NpmCliRepairDecision.MissingShimRepair, decision);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Missing_package_is_truly_uninstalled_and_never_auto_installed(string cliType)
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            cliType,
            ExecutableAvailable: false,
            PackageDirectoryPresent: false,
            ShimPresent: false,
            ObservedAt: Now,
            LastAttemptAt: null));

        Assert.Equal(NpmCliRepairDecision.TrulyUninstalled, decision);
    }

    [Fact]
    public void Attempt_inside_one_hour_is_rate_limited()
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            CliTypes.Claude,
            ExecutableAvailable: false,
            PackageDirectoryPresent: true,
            ShimPresent: false,
            ObservedAt: Now,
            LastAttemptAt: Now.AddMinutes(-59)));

        Assert.Equal(NpmCliRepairDecision.RateLimited, decision);
    }

    [Fact]
    public void Attempt_at_one_hour_boundary_is_allowed()
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            CliTypes.Codex,
            ExecutableAvailable: false,
            PackageDirectoryPresent: true,
            ShimPresent: false,
            ObservedAt: Now,
            LastAttemptAt: Now.AddHours(-1)));

        Assert.Equal(NpmCliRepairDecision.MissingShimRepair, decision);
    }

    [Fact]
    public void Existing_shim_with_broken_executable_is_not_misclassified_as_missing_shim()
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            CliTypes.Claude,
            ExecutableAvailable: false,
            PackageDirectoryPresent: true,
            ShimPresent: true,
            ObservedAt: Now,
            LastAttemptAt: null));

        Assert.Equal(NpmCliRepairDecision.Unsupported, decision);
    }

    [Fact]
    public void Available_executable_never_repairs()
    {
        var decision = NpmCliShimRepairPolicy.Decide(new NpmCliInstallFacts(
            CliTypes.Claude,
            ExecutableAvailable: true,
            PackageDirectoryPresent: true,
            ShimPresent: false,
            ObservedAt: Now,
            LastAttemptAt: null));

        Assert.Equal(NpmCliRepairDecision.Available, decision);
    }
}
