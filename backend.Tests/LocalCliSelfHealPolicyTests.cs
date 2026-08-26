using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "local-cli-self-heal-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_DistinguishesMissingShimFromTrulyUninstalled()
    {
        WritePackage("@anthropic-ai/claude-code", "2.1.231");

        var missingShim = LocalCliSelfHealPolicy.Inspect(
            "claude",
            "claude",
            "@anthropic-ai/claude-code",
            _root,
            isWindows: true);
        var uninstalled = LocalCliSelfHealPolicy.Inspect(
            "codex",
            "codex",
            "@openai/codex",
            _root,
            isWindows: true);

        Assert.Equal(LocalCliInstallState.MissingShimWithPackagePresent, missingShim.State);
        Assert.True(missingShim.PackagePresent);
        Assert.False(missingShim.ExpectedShimPresent);
        Assert.Equal("2.1.231", missingShim.PackageVersion);
        Assert.EndsWith("claude.cmd", missingShim.ExpectedShimPath);
        Assert.Equal(LocalCliInstallState.TrulyUninstalled, uninstalled.State);
        Assert.False(uninstalled.PackagePresent);
    }

    [Fact]
    public void Inspect_DoesNotRepairPresentButBrokenShimOrCustomPath()
    {
        WritePackage("@openai/codex", "0.42.0");
        File.WriteAllText(Path.Combine(_root, "codex.cmd"), "broken fixture");

        var brokenShim = LocalCliSelfHealPolicy.Inspect(
            "codex",
            "codex",
            "@openai/codex",
            _root,
            isWindows: true);
        var customPath = LocalCliSelfHealPolicy.Inspect(
            "codex",
            Path.Combine(_root, "custom", "codex.exe"),
            "@openai/codex",
            _root,
            isWindows: true);

        Assert.Equal(LocalCliInstallState.Unavailable, brokenShim.State);
        Assert.True(brokenShim.ExpectedShimPresent);
        Assert.Equal(LocalCliInstallState.Unavailable, customPath.State);
    }

    [Fact]
    public void Inspect_UsesThePortableShimNameOffWindows()
    {
        WritePackage("@openai/codex", "0.42.0");

        var inspection = LocalCliSelfHealPolicy.Inspect(
            "codex",
            "codex",
            "@openai/codex",
            _root,
            isWindows: false);

        Assert.Equal(LocalCliInstallState.MissingShimWithPackagePresent, inspection.State);
        Assert.EndsWith(Path.Combine(_root, "codex"), inspection.ExpectedShimPath);
    }

    [Fact]
    public void RepairAttemptAllowed_OpensAtTheOneHourBoundary()
    {
        var attemptedAt = DateTimeOffset.Parse("2026-08-18T10:00:00Z");

        Assert.False(LocalCliSelfHealPolicy.RepairAttemptAllowed(
            attemptedAt,
            attemptedAt.AddMinutes(59),
            LocalCliSelfHealService.RepairAttemptWindow));
        Assert.True(LocalCliSelfHealPolicy.RepairAttemptAllowed(
            attemptedAt,
            attemptedAt.AddHours(1),
            LocalCliSelfHealService.RepairAttemptWindow));
    }

    private void WritePackage(string packageName, string version)
    {
        var packagePath = Path.Combine(
            _root,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(
            Path.Combine(packagePath, "package.json"),
            $$"""{"version":"{{version}}"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
