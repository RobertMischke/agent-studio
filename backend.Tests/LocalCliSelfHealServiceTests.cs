using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliSelfHealServiceTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code", "2.1.234")]
    [InlineData("codex", "@openai", "codex", "0.144.1")]
    public void Inspection_distinguishes_missing_shim_from_uninstalled_on_every_os(
        string cliType,
        string scope,
        string package,
        string version)
    {
        using var temp = new TempDirectory();
        var prefix = Path.Combine(temp.Path, "npm");
        var root = Path.Combine(prefix, "node_modules");
        var packagePath = Path.Combine(root, scope, package);
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), $$"""{"version":"{{version}}"}""");

        var missingShim = NpmCliShimInspectionPolicy.Inspect(
            cliType, cliType, commandAvailable: false, prefix, root);

        Assert.Equal(NpmCliInstallState.MissingOrBrokenShimWithPackagePresent, missingShim.State);
        Assert.Equal(version, missingShim.PackageVersion);
        Assert.Empty(missingShim.ExistingShims);
        Assert.Contains("every", missingShim.Detail);

        Directory.Delete(packagePath, recursive: true);
        var uninstalled = NpmCliShimInspectionPolicy.Inspect(
            cliType, cliType, commandAvailable: false, prefix, root);

        Assert.Equal(NpmCliInstallState.TrulyUninstalled, uninstalled.State);
    }

    [Fact]
    public void Inspection_never_reinstalls_an_explicit_binary_override()
    {
        using var temp = new TempDirectory();
        var inspection = NpmCliShimInspectionPolicy.Inspect(
            "claude",
            Path.Combine(temp.Path, "claude.exe"),
            commandAvailable: false,
            Path.Combine(temp.Path, "npm"),
            Path.Combine(temp.Path, "npm", "node_modules"));

        Assert.Equal(NpmCliInstallState.Unsupported, inspection.State);
        Assert.Contains("explicit override", inspection.Detail);
    }

    [Fact]
    public async Task Package_present_repair_records_versions_activity_and_restored_shim()
    {
        using var temp = new TempDirectory();
        var layout = PreparePackage(temp.Path, "@anthropic-ai", "claude-code", "2.1.234");
        var now = new DateTimeOffset(2026, 8, 18, 9, 15, 0, TimeSpan.Zero);
        var available = true;
        var version = "2.1.231 (Claude Code)";
        var installCalls = 0;
        var service = CreateService(temp.Path, () => now, async (command, arguments, _, _) =>
        {
            await Task.Yield();
            if (arguments.SequenceEqual(new[] { "prefix", "-g" }))
                return new LocalCliCommandResult(0, $"{layout.Prefix}\nnpm WARN trailing diagnostic", null);
            if (arguments.SequenceEqual(new[] { "root", "-g" }))
                return new LocalCliCommandResult(0, $"{layout.Root}\nnpm WARN trailing diagnostic", null);
            installCalls++;
            File.WriteAllText(Path.Combine(layout.Prefix, "claude.cmd"), "@echo off");
            File.WriteAllText(
                Path.Combine(layout.PackagePath, "package.json"),
                "{\"version\":\"2.1.234\"}");
            available = true;
            version = "2.1.234 (Claude Code)";
            return new LocalCliCommandResult(0, "changed 1 package", null);
        });

        await service.ProbeAndRepairAsync(
            "claude", "claude", () => (available, version, "claude"), CancellationToken.None);
        available = false;
        now = now.AddMinutes(5);

        var outcome = await service.ProbeAndRepairAsync(
            "claude", "claude", () => (available, available ? version : null, "claude"), CancellationToken.None);

        Assert.True(outcome.Repaired);
        Assert.Equal(1, installCalls);
        var status = Assert.Single(service.Snapshot());
        Assert.Equal("repaired", status.State);
        Assert.Equal("2.1.231 (Claude Code)", status.CliVersionBefore);
        Assert.Equal("2.1.234 (Claude Code)", status.CliVersionAfter);
        Assert.Contains("CLI repaired at", status.Detail);

        var journal = File.ReadAllLines(service.JournalPath);
        Assert.Contains(journal, line => line.Contains("\"event\":\"repair-attempt\"", StringComparison.Ordinal));
        var success = Assert.Single(journal, line => line.Contains("\"event\":\"repair-succeeded\"", StringComparison.Ordinal));
        Assert.Contains("\"exists\":false", success);
        Assert.Contains("\"exists\":true", success);
        Assert.Contains("2.1.231 (Claude Code)", success);
        Assert.Contains("2.1.234 (Claude Code)", success);
    }

    [Fact]
    public async Task Failed_attempt_is_limited_to_one_per_hour_across_service_restart()
    {
        using var temp = new TempDirectory();
        var layout = PreparePackage(temp.Path, "@openai", "codex", "0.144.1");
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var installCalls = 0;
        LocalCliCommandLauncher launcher = (_, arguments, _, _) =>
        {
            if (arguments.SequenceEqual(new[] { "prefix", "-g" }))
                return Task.FromResult(new LocalCliCommandResult(0, layout.Prefix, null));
            if (arguments.SequenceEqual(new[] { "root", "-g" }))
                return Task.FromResult(new LocalCliCommandResult(0, layout.Root, null));
            installCalls++;
            return Task.FromResult(new LocalCliCommandResult(1, "npm interrupted", "npm interrupted"));
        };
        var service = CreateService(temp.Path, () => now, launcher);

        var first = await service.ProbeAndRepairAsync(
            "codex", "codex", () => (false, null, "codex"), CancellationToken.None);
        Assert.False(first.Available);
        Assert.Equal(1, installCalls);
        Assert.Equal("failed", Assert.Single(service.Snapshot()).State);

        now = now.AddMinutes(30);
        var restarted = CreateService(temp.Path, () => now, launcher);
        var throttled = await restarted.ProbeAndRepairAsync(
            "codex", "codex", () => (false, null, "codex"), CancellationToken.None);
        Assert.True(throttled.Throttled);
        Assert.Equal(1, installCalls);

        now = now.AddMinutes(31);
        await restarted.ProbeAndRepairAsync(
            "codex", "codex", () => (false, null, "codex"), CancellationToken.None);
        Assert.Equal(2, installCalls);

        now = now.AddMinutes(1);
        var recovered = await restarted.ProbeAndRepairAsync(
            "codex", "codex", () => (true, "0.144.1", "codex"), CancellationToken.None);
        Assert.True(recovered.Available);
        Assert.Empty(restarted.Snapshot());
        Assert.Contains(
            File.ReadLines(restarted.JournalPath),
            line => line.Contains("\"event\":\"capability-restored\"", StringComparison.Ordinal));
    }

    private static LocalCliSelfHealService CreateService(
        string root,
        Func<DateTimeOffset> clock,
        LocalCliCommandLauncher launcher)
        => new(
            NullLogger<LocalCliSelfHealService>.Instance,
            Path.Combine(root, "cli-repair-journal.jsonl"),
            clock,
            launcher,
            isWindows: true);

    private static PackageLayout PreparePackage(
        string root,
        string scope,
        string package,
        string version)
    {
        var prefix = Path.Combine(root, "npm");
        var npmRoot = Path.Combine(prefix, "node_modules");
        var packagePath = Path.Combine(npmRoot, scope, package);
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), $$"""{"version":"{{version}}"}""");
        return new PackageLayout(prefix, npmRoot, packagePath);
    }

    private sealed record PackageLayout(string Prefix, string Root, string PackagePath);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"local-cli-self-heal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
