using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliRepairServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-repair-tests-").FullName;

    [Theory]
    [InlineData(CliTypes.Claude, "@anthropic-ai", "claude-code", "2.1.234")]
    [InlineData(CliTypes.Codex, "@openai", "codex", "0.146.0")]
    public void InspectGlobalInstall_DetectsPackagePresentWithEveryShimMissing(
        string cliType,
        string scope,
        string package,
        string version)
    {
        var npmBin = Path.Combine(_root, cliType);
        var packageDir = Path.Combine(npmBin, "node_modules", scope, package);
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), $$"""{"version":"{{version}}"}""");

        var result = LocalCliRepairService.InspectGlobalInstall(cliType, npmBin);

        Assert.Equal(NpmCliInstallKind.MissingShimWithPackage, result.Kind);
        Assert.Equal(version, result.PackageVersion);
        Assert.Empty(result.PresentShims);
        Assert.Contains("package", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shims are missing", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectGlobalInstall_DistinguishesTrueUninstall()
    {
        var npmBin = Path.Combine(_root, "uninstalled");
        Directory.CreateDirectory(npmBin);

        var result = LocalCliRepairService.InspectGlobalInstall(CliTypes.Claude, npmBin);

        Assert.Equal(NpmCliInstallKind.PackageMissing, result.Kind);
        Assert.DoesNotContain("shims are missing", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("claude", "claude.cmd")]
    [InlineData("codex", "codex.exe")]
    public void InspectGlobalInstall_DoesNotClassifyAnIntactShimAsMissing(
        string cliType,
        string shim)
    {
        var npmBin = Path.Combine(_root, cliType + "-shim");
        var package = cliType == CliTypes.Claude
            ? Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code")
            : Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(npmBin, shim), "shim");

        var result = LocalCliRepairService.InspectGlobalInstall(cliType, npmBin);

        Assert.Equal(NpmCliInstallKind.ShimPresent, result.Kind);
        Assert.Single(result.PresentShims);
    }

    [Fact]
    public void CanAttempt_EnforcesTheFullOneHourBoundary()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        Assert.False(LocalCliRepairService.CanAttempt(attemptedAt, attemptedAt.AddMinutes(59).AddSeconds(59)));
        Assert.True(LocalCliRepairService.CanAttempt(attemptedAt, attemptedAt.AddHours(1)));
    }

    [Fact]
    public void BuildWindowsNpmCommand_PreservesASpacedNpmCmdPath()
    {
        var command = LocalCliRepairService.BuildWindowsNpmCommand(
            @"C:\Program Files\nodejs\npm.cmd",
            "@anthropic-ai/claude-code");

        Assert.Equal(
            "\"\"C:\\Program Files\\nodejs\\npm.cmd\" install --global @anthropic-ai/claude-code\"",
            command);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
