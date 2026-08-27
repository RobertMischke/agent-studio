using AgentStudio.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliRepairServiceTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code", "2.1.231")]
    [InlineData("codex", "@openai", "codex", "1.2.3")]
    public void Inspect_distinguishes_missing_shim_from_uninstalled_package(
        string cliType,
        string scope,
        string package,
        string version)
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", scope, package);
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), $$"""{"version":"{{version}}"}""");

        var installed = LocalCliRepairService.Inspect(cliType, cliType, npmBin);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackagePresent, installed.State);
        Assert.Equal(version, installed.PackageVersion);

        Directory.Delete(packageDir, recursive: true);
        var absent = LocalCliRepairService.Inspect(cliType, cliType, npmBin);
        Assert.Equal(NpmCliInstallState.TrulyUninstalled, absent.State);
    }

    [Fact]
    public void Inspect_does_not_reinstall_when_any_shim_is_present()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        Directory.CreateDirectory(Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code"));
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "shim");

        var inspection = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.PackagePresentWithShim, inspection.State);
    }

    [Fact]
    public void Inspect_does_not_repair_a_custom_missing_path()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        Directory.CreateDirectory(Path.Combine(
            npmBin, "node_modules", "@openai", "codex"));

        var inspection = LocalCliRepairService.Inspect(
            "codex",
            Path.Combine(temp.Path, "custom", "codex.cmd"),
            npmBin);

        Assert.Equal(NpmCliInstallState.Unsupported, inspection.State);
    }

    [Fact]
    public void Attempt_budget_allows_only_one_attempt_per_hour()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        Assert.False(LocalCliRepairService.AttemptAllowed(
            attemptedAt.AddMinutes(59), attemptedAt));
        Assert.True(LocalCliRepairService.AttemptAllowed(
            attemptedAt.AddHours(1), attemptedAt));
    }

    [Fact]
    public async Task Probe_repairs_missing_shim_journals_versions_and_suppresses_repeat()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(
            Path.Combine(packageDir, "package.json"),
            "{\"version\":\"2.1.231\"}");
        var localAppData = Path.Combine(temp.Path, "local-appdata");
        var npmLogs = Path.Combine(localAppData, "npm-cache", "_logs");
        Directory.CreateDirectory(npmLogs);
        var npmLog = Path.Combine(npmLogs, "2026-08-18T10_00_00_000Z-debug-0.log");
        File.WriteAllText(
            npmLog,
            "10 verbose argv npm update --global @anthropic-ai/claude-code");
        File.SetLastWriteTimeUtc(npmLog, new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc));
        var shim = Path.Combine(npmBin, "claude.cmd");
        var installer = new FakeInstaller(_ => File.WriteAllText(shim, "shim"));
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => localAppData,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => File.Exists(shim)
                ? (true, "2.1.234", shim)
                : (false, null, "claude");

        var result = await service.ProbeAndRepairAsync(
            "claude", "2.1.231", Probe, CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(1, installer.Calls);
        var status = Assert.Single(service.Current());
        Assert.Equal("repaired", status.Outcome);
        Assert.Equal("2.1.231", status.VersionBefore);
        Assert.Equal("2.1.234", status.VersionAfter);
        var journalText = File.ReadAllText(journal);
        Assert.Contains("missing-shim-with-package-present", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.231", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.234", journalText, StringComparison.Ordinal);
        Assert.Contains("npm update --global @anthropic-ai/claude-code", journalText, StringComparison.Ordinal);

        File.Delete(shim);
        now = now.AddMinutes(59);
        var restartedService = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => localAppData,
            journal);
        Assert.Equal("repaired", Assert.Single(restartedService.Current()).Outcome);
        await restartedService.ProbeAndRepairAsync(
            "claude", "2.1.234", Probe, CancellationToken.None);
        Assert.Equal(1, installer.Calls);
    }

    private sealed class FakeInstaller(Action<string> onInstall) : NpmGlobalInstaller
    {
        public int Calls { get; private set; }

        public override Task<NpmGlobalInstallResult> InstallAsync(
            string packageName,
            CancellationToken ct)
        {
            Calls++;
            onInstall(packageName);
            return Task.FromResult(new NpmGlobalInstallResult(true, 0, "installed", ""));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agent-studio-cli-repair-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
