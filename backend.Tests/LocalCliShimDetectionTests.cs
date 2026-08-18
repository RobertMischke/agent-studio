using AgentStudio.HostHealth;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Shim detection, end to end but host-portable: the name rules, the npm
/// layout resolution for both platforms, and the inspector reading a fake npm
/// global bin under a temporary directory. Nothing here needs Windows or a
/// real npm install, which is what makes the Windows-only defect testable in
/// CI.
/// </summary>
public class LocalCliShimDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agt-2673-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        GC.SuppressFinalize(this);
    }

    // ===== Name rules =====

    [Theory]
    [InlineData("claude", true, new[] { "claude", "claude.cmd", "claude.exe" })]
    [InlineData("claude", false, new[] { "claude" })]
    public void LaunchableShims_covers_what_the_OS_can_actually_start(string command, bool isWindows, string[] expected)
        => Assert.Equal(expected, LocalCliShimNames.LaunchableShims(command, isWindows));

    [Fact]
    public void A_lone_surviving_ps1_launcher_does_not_count_as_launchable()
        => Assert.DoesNotContain("claude.ps1", LocalCliShimNames.LaunchableShims("claude", isWindows: true));

    [Theory]
    [InlineData(".claude-2shlnT4k", true)]
    [InlineData(".claude.cmd-A8DH7lDq", true)]
    [InlineData(".claude.ps1-Phb6s52t", true)]
    [InlineData(".claude.exe-9dK2", true)]
    [InlineData("claude.cmd", false)]        // the canonical shim
    [InlineData(".claudeconfig", false)]     // an unrelated dotfile, no -<random> tail
    [InlineData(".claude.json-", false)]     // dash present but nothing after it
    [InlineData(".codex-abc", false)]        // a different CLI's orphan
    public void IsOrphanShim_recognises_npm_atomic_rename_leftovers(string fileName, bool expected)
        => Assert.Equal(expected, LocalCliShimNames.IsOrphanShim(fileName, "claude"));

    // ===== Layout resolution =====

    [Fact]
    public void Windows_resolves_the_global_bin_to_the_APPDATA_npm_directory()
    {
        var layout = NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment { IsWindows = true, AppData = @"C:\Users\op\AppData\Roaming", LocalAppData = @"C:\Users\op\AppData\Local" },
            _ => false);

        Assert.True(layout.Resolved);
        Assert.Equal(Path.Combine(@"C:\Users\op\AppData\Roaming", "npm"), layout.BinDirectory);
        Assert.Equal(Path.Combine(@"C:\Users\op\AppData\Roaming", "npm", "node_modules"), layout.NodeModulesDirectory);
        Assert.Equal(Path.Combine(@"C:\Users\op\AppData\Local", "npm-cache", "_logs"), layout.LogsDirectory);
    }

    [Fact]
    public void Posix_puts_the_bin_and_node_modules_under_the_npm_prefix()
    {
        var layout = NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment { IsWindows = false, NpmConfigPrefix = "/opt/npm-global", Home = "/home/op" },
            _ => false);

        Assert.Equal("/opt/npm-global/bin", layout.BinDirectory);
        Assert.Equal(Path.Combine("/opt/npm-global", "lib", "node_modules"), layout.NodeModulesDirectory);
        Assert.Equal(Path.Combine("/home/op", ".npm", "_logs"), layout.LogsDirectory);
    }

    [Fact]
    public void Posix_falls_back_to_the_first_prefix_that_actually_has_node_modules()
    {
        var layout = NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment { IsWindows = false, Home = "/home/op" },
            path => path == Path.Combine("/usr/local", "lib", "node_modules"));

        Assert.Equal(Path.Combine("/usr/local", "bin"), layout.BinDirectory);
    }

    [Fact]
    public void A_host_without_a_global_npm_install_resolves_to_nothing()
    {
        var layout = NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment { IsWindows = false, Home = "/home/op" },
            _ => false);

        Assert.False(layout.Resolved);
    }

    [Fact]
    public void An_explicit_override_wins_over_platform_detection()
    {
        var layout = NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment { ConfiguredBin = "/srv/npm-bin/", IsWindows = true, AppData = @"C:\ignored" },
            _ => false);

        Assert.Equal("/srv/npm-bin", layout.BinDirectory);
        Assert.Equal(Path.Combine("/srv/npm-bin", "node_modules"), layout.NodeModulesDirectory);
    }

    // ===== Inspector against a fake npm global bin =====

    private static readonly LocalCliPackage Claude = new("claude", "claude", "@anthropic-ai/claude-code");

    [Fact]
    public void The_observed_breakage_reads_as_package_present_and_shim_gone()
    {
        var inspector = Fake(withShim: false, withPackage: true, packageVersion: "2.1.234");

        var facts = inspector.Inspect(Claude, versionProbeOk: false, probedVersion: null);

        Assert.True(facts.PackagePresent);
        Assert.False(facts.ShimPresent);
        Assert.False(facts.OrphanShimsPresent);
        Assert.Equal("2.1.234", facts.PackageVersion);
        Assert.Equal(
            LocalCliInstallState.ShimMissingPackagePresent,
            LocalCliInstallDiagnosis.Diagnose(facts).State);
    }

    [Fact]
    public void An_uninstalled_cli_reads_as_neither_package_nor_shim()
    {
        var inspector = Fake(withShim: false, withPackage: false);

        var facts = inspector.Inspect(Claude, versionProbeOk: false, probedVersion: null);

        Assert.False(facts.PackagePresent);
        Assert.False(facts.ShimPresent);
        Assert.Equal(LocalCliInstallState.NotInstalled, LocalCliInstallDiagnosis.Diagnose(facts).State);
    }

    [Fact]
    public void Orphan_shims_on_disk_are_reported_separately_from_a_missing_shim()
    {
        var inspector = Fake(withShim: false, withPackage: true);
        File.WriteAllText(Path.Combine(_root, "bin", ".claude.cmd-A8DH7lDq"), "orphan");

        var facts = inspector.Inspect(Claude, versionProbeOk: false, probedVersion: null);

        Assert.True(facts.OrphanShimsPresent);
        Assert.False(facts.ShimPresent);
        Assert.Equal(LocalCliRepairAction.RestoreShims, LocalCliInstallDiagnosis.Diagnose(facts).Action);
    }

    [Fact]
    public void A_torn_package_json_still_yields_a_usable_diagnosis()
    {
        var inspector = Fake(withShim: false, withPackage: true);
        File.WriteAllText(PackageManifest(), "{ this is not json");

        var facts = inspector.Inspect(Claude, versionProbeOk: false, probedVersion: null);

        Assert.True(facts.PackagePresent);
        Assert.Null(facts.PackageVersion);
        Assert.Equal(LocalCliInstallState.ShimMissingPackagePresent, LocalCliInstallDiagnosis.Diagnose(facts).State);
    }

    [Fact]
    public void An_unresolved_layout_reports_that_it_cannot_tell()
    {
        var inspector = new LocalCliInstallInspector(
            NullLogger<LocalCliInstallInspector>.Instance, NpmGlobalLayout.Unresolved, isWindows: false);

        var facts = inspector.Inspect(Claude, versionProbeOk: false, probedVersion: null);

        Assert.False(facts.NpmGlobalBinResolved);
        Assert.Equal(LocalCliInstallState.Unknown, LocalCliInstallDiagnosis.Diagnose(facts).State);
    }

    [Fact]
    public void Npm_logs_after_the_observation_are_excluded_from_the_evidence()
    {
        var inspector = Fake(withShim: false, withPackage: true);
        var logsDirectory = Path.Combine(_root, "_logs");
        Directory.CreateDirectory(logsDirectory);
        var observedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

        WriteLog(logsDirectory, "inside.log", observedAt.AddMinutes(-4));
        WriteLog(logsDirectory, "too-old.log", observedAt.AddHours(-4));
        WriteLog(logsDirectory, "our-own-repair.log", observedAt.AddMinutes(2));

        var activity = inspector.RecentNpmActivity(observedAt, TimeSpan.FromMinutes(30));

        Assert.Equal(["inside.log"], activity.Select(entry => entry.Name));
    }

    private static void WriteLog(string directory, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "npm debug log");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    private string PackageManifest()
        => Path.Combine(_root, "node_modules", "@anthropic-ai", "claude-code", "package.json");

    private LocalCliInstallInspector Fake(bool withShim, bool withPackage, string? packageVersion = "2.1.234")
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        if (withShim) File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh");
        if (withPackage)
        {
            var manifest = PackageManifest();
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest, $$"""{"name":"@anthropic-ai/claude-code","version":"{{packageVersion}}"}""");
        }

        return new LocalCliInstallInspector(
            NullLogger<LocalCliInstallInspector>.Instance,
            new NpmGlobalLayout(bin, Path.Combine(_root, "node_modules"), Path.Combine(_root, "_logs")),
            isWindows: false);
    }
}
