using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class NpmCliShimDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "npm-cli-shim-detection-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, true, true, NpmCliInstallState.Healthy)]
    [InlineData(false, false, false, NpmCliInstallState.TrulyUninstalled)]
    [InlineData(false, true, false, NpmCliInstallState.MissingShimWithPackagePresent)]
    [InlineData(false, true, true, NpmCliInstallState.UnavailableWithShimPresent)]
    public void Classify_DistinguishesMissingShimFromUninstalledAndBrokenBinary(
        bool cliAvailable,
        bool packagePresent,
        bool commandShimPresent,
        NpmCliInstallState expected)
    {
        Assert.Equal(expected, NpmCliShimDetection.Classify(
            cliAvailable,
            packagePresent,
            commandShimPresent));
    }

    [Fact]
    public void AttemptAllowed_EnforcesOneAttemptPerCliPerHour()
    {
        var now = DateTimeOffset.Parse("2026-08-18T10:00:00Z");
        Assert.True(LocalCliSelfHeal.AttemptAllowed(now, null));
        Assert.False(LocalCliSelfHeal.AttemptAllowed(now, now.AddMinutes(-59)));
        Assert.True(LocalCliSelfHeal.AttemptAllowed(now, now.AddHours(-1)));
    }

    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code", "2.1.231")]
    [InlineData("codex", "@openai", "codex", "0.70.0")]
    public void Inspect_PackagePresentWithoutCmdShim_IsRepairable(
        string cliType,
        string scope,
        string package,
        string version)
    {
        var npmBin = Path.Combine(_root, "npm");
        var packagePath = Path.Combine(npmBin, "node_modules", scope, package);
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(
            Path.Combine(packagePath, "package.json"),
            JsonSerializer.Serialize(new { version }));

        var snapshot = NpmCliShimDetection.Inspect(
            cliType,
            npmBin,
            npmCachePath: null,
            cliAvailable: false,
            DateTimeOffset.Parse("2026-08-18T10:00:00Z"));

        Assert.Equal(NpmCliInstallState.MissingShimWithPackagePresent, snapshot.State);
        Assert.True(snapshot.PackagePresent);
        Assert.Equal(version, snapshot.PackageVersion);
        Assert.Contains(snapshot.Shims, shim => shim.Name == cliType + ".cmd" && !shim.Exists);
    }

    [Fact]
    public void Inspect_ExistingCmdShim_DoesNotMisclassifyBinaryFailureAsMissingShim()
    {
        var npmBin = Path.Combine(_root, "npm");
        var packagePath = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), "{\"version\":\"2.1.234\"}");
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "@echo off");

        var snapshot = NpmCliShimDetection.Inspect(
            "claude",
            npmBin,
            npmCachePath: null,
            cliAvailable: false,
            DateTimeOffset.Parse("2026-08-18T10:00:00Z"));

        Assert.Equal(NpmCliInstallState.UnavailableWithShimPresent, snapshot.State);
    }

    [Fact]
    public void Inspect_CapturesRecentRelevantNpmActivityAndRedactsTokens()
    {
        var npmBin = Path.Combine(_root, "npm");
        var packagePath = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var npmCache = Path.Combine(_root, "npm-cache");
        var logDir = Path.Combine(npmCache, "_logs");
        Directory.CreateDirectory(packagePath);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), "{\"version\":\"2.1.231\"}");
        var logPath = Path.Combine(logDir, "2026-08-18-debug.log");
        File.WriteAllLines(logPath,
        [
            "npm install @anthropic-ai/claude-code authorization: secret-value",
            "postinstall completed",
        ]);
        File.SetLastWriteTimeUtc(logPath, DateTime.Parse("2026-08-18T09:55:00Z").ToUniversalTime());

        var snapshot = NpmCliShimDetection.Inspect(
            "claude",
            npmBin,
            npmCache,
            cliAvailable: false,
            DateTimeOffset.Parse("2026-08-18T10:00:00Z"));

        var activity = Assert.Single(snapshot.RecentNpmActivity);
        Assert.Contains(activity.RelevantTail, line => line.Contains("npm install"));
        Assert.DoesNotContain(activity.RelevantTail, line => line.Contains("secret-value"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
