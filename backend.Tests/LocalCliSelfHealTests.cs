using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agent-studio-cli-shim-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_DistinguishesMissingShimWithPackagePresent()
    {
        WritePackage("@anthropic-ai", "claude-code", "2.1.234");

        var result = WindowsNpmGlobalInstallInspector.Inspect(
            _root, "claude", "@anthropic-ai", "claude-code");

        Assert.Equal(NpmGlobalCliInstallState.PackagePresentShimMissing, result.State);
        Assert.Equal("2.1.234", result.PackageVersion);
        Assert.Contains(Path.Combine(_root, "claude.cmd"), result.MissingShimPaths);
    }

    [Fact]
    public void Inspect_DistinguishesTrulyUninstalledPackage()
    {
        Directory.CreateDirectory(_root);

        var result = WindowsNpmGlobalInstallInspector.Inspect(
            _root, "codex", "@openai", "codex");

        Assert.Equal(NpmGlobalCliInstallState.PackageAbsent, result.State);
        Assert.Null(result.PackageVersion);
    }

    [Fact]
    public void Inspect_DoesNotClassifyAnotherExecutableFailureAsMissingShim()
    {
        WritePackage("@openai", "codex", "0.88.0");
        File.WriteAllText(Path.Combine(_root, "codex.cmd"), "@echo off");

        var result = WindowsNpmGlobalInstallInspector.Inspect(
            _root, "codex", "@openai", "codex");

        Assert.Equal(NpmGlobalCliInstallState.ShimPresentOrDifferentFailure, result.State);
    }

    [Fact]
    public void RepairPolicy_AllowsOnlyOneAttemptWithinAnHour()
    {
        var now = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

        Assert.True(LocalCliRepairPolicy.CanAttempt(null, now, TimeSpan.FromHours(1)));
        Assert.False(LocalCliRepairPolicy.CanAttempt(now, now.AddMinutes(59), TimeSpan.FromHours(1)));
        Assert.True(LocalCliRepairPolicy.CanAttempt(now, now.AddHours(1), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void RepairPolicy_RefusesAnUnrelatedExplicitPath()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "portable-cli", "claude.exe");

        Assert.False(LocalCliRepairPolicy.ShouldRepairPath(explicitPath, _root));
        Assert.True(LocalCliRepairPolicy.ShouldRepairPath("claude", _root));
        Assert.True(LocalCliRepairPolicy.ShouldRepairPath(Path.Combine(_root, "claude.cmd"), _root));
    }

    [Fact]
    public void NpmActivitySummary_KeepsCommandMarkersButDropsCredentialLines()
    {
        Directory.CreateDirectory(_root);
        var log = Path.Combine(_root, "npm-debug.log");
        File.WriteAllLines(log,
        [
            "0 info using npm@11.0.0",
            "1 verbose title npm install @anthropic-ai/claude-code",
            "2 verbose argv install --global @anthropic-ai/claude-code",
            "3 verbose argv --token top-secret",
            "4 silly config load C:/Users/operator/.npmrc",
            "5 verbose exit 0",
        ]);

        var summary = WindowsNpmGlobalInstallInspector.SummarizeNpmLog(log);

        Assert.Contains(summary, line => line.Contains("verbose title", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.Contains("verbose exit", StringComparison.Ordinal));
        Assert.DoesNotContain(summary, line => line.Contains("top-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(summary, line => line.Contains(".npmrc", StringComparison.Ordinal));
    }

    private void WritePackage(string scope, string packageName, string version)
    {
        var directory = Path.Combine(_root, "node_modules", scope, packageName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), $$"""{"version":"{{version}}"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
