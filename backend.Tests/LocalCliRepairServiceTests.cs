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
    public void Inspect_requires_the_windows_command_shim()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        Directory.CreateDirectory(Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code"));
        File.WriteAllText(Path.Combine(npmBin, "claude"), "shell shim");

        var missingCommandShim = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackagePresent, missingCommandShim.State);

        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");
        var inspection = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.PackagePresentWithShim, inspection.State);
    }

    [Fact]
    public void Inspect_detects_claude_launcher_stub_and_records_native_package_evidence()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        var nativePackage = Path.Combine(
            packageDir,
            "node_modules",
            "@anthropic-ai",
            "claude-code-win32-x64");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        Directory.CreateDirectory(nativePackage);
        File.WriteAllText(
            Path.Combine(packageDir, "package.json"),
            "{\"version\":\"2.1.263\",\"bin\":{\"claude\":\"bin/claude.exe\"}}");
        File.WriteAllBytes(launcher, new byte[500]);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");

        var inspection = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.LauncherStubWithPackageAndShim, inspection.State);
        Assert.Equal(launcher, inspection.LauncherPath);
        Assert.Equal(500, inspection.LauncherSizeBytes);
        Assert.Equal(nativePackage, inspection.NativePackageDirectory);
    }

    [Fact]
    public void Inspect_detects_claude_launcher_stub_from_failed_version_output()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"2.1.263\"}");
        File.WriteAllBytes(launcher, new byte[LocalCliRepairService.LauncherStubThresholdBytes]);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");

        var inspection = LocalCliRepairService.Inspect(
            "claude",
            "claude",
            npmBin,
            "Error: claude native binary not installed.");

        Assert.Equal(NpmCliInstallState.LauncherStubWithPackageAndShim, inspection.State);
        Assert.Contains("native binary not installed", inspection.VersionProbeOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NpmGlobalInstallMode.Install, false)]
    [InlineData(NpmGlobalInstallMode.ForceRelink, true)]
    public void Installer_arguments_distinguish_install_from_force_relink(
        NpmGlobalInstallMode mode,
        bool expectsForce)
    {
        var arguments = NpmGlobalInstaller.BuildArguments("@openai/codex", mode);

        Assert.Equal(expectsForce, arguments.Contains("--force"));
        Assert.Equal("@openai/codex", arguments[2]);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Installer_resolution_returns_an_explicit_npm_that_passed_version_preflight()
    {
        var resolution = await NpmGlobalInstaller.ResolveNpmExecutableAsync(CancellationToken.None);

        var command = Assert.IsType<NpmExecutable>(resolution.Command);
        Assert.True(Path.IsPathRooted(command.ExecutablePath));
        Assert.True(File.Exists(command.ExecutablePath));
        Assert.True(Directory.Exists(command.WorkingDirectory));
        Assert.False(string.IsNullOrWhiteSpace(command.Version));
    }

    [Fact]
    public void Installer_resolution_prefers_npm_shipped_with_the_active_node_install()
    {
        using var temp = new TempDirectory();
        var nodeDirectory = Path.Combine(temp.Path, "nodejs");
        var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var appDataNpm = Path.Combine(temp.Path, "appdata", "npm", "npm.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
        Directory.CreateDirectory(Path.GetDirectoryName(appDataNpm)!);
        File.WriteAllText(Path.Combine(nodeDirectory, "node.exe"), "node");
        File.WriteAllText(npmCli, "npm");
        File.WriteAllText(appDataNpm, "npm");

        var candidates = NpmGlobalInstaller.NpmExecutableCandidates(
            true,
            nodeDirectory,
            Path.Combine(temp.Path, "appdata"),
            null,
            null,
            null);

        var preferred = Assert.IsType<NpmExecutable>(candidates.First());
        Assert.Equal(Path.Combine(nodeDirectory, "node.exe"), preferred.ExecutablePath);
        Assert.Equal([npmCli], preferred.PrefixArguments);
        Assert.Equal(nodeDirectory, preferred.WorkingDirectory);
    }

    [Fact]
    public async Task Installer_reports_typed_npm_unavailable_without_leaking_module_errors()
    {
        var installer = new NpmGlobalInstaller(_ => Task.FromResult(
            new NpmExecutableResolution(
                null,
                "npm unavailable: no candidate passed 'npm --version'.")));

        var result = await installer.InstallAsync(
            "@openai/codex",
            NpmGlobalInstallMode.ForceRelink,
            CancellationToken.None);

        Assert.Equal(NpmGlobalInstallOutcome.NpmUnavailable, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Contains("npm unavailable", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("MODULE_NOT_FOUND", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NpmCliInstallState.TrulyUninstalled, NpmGlobalInstallMode.Install)]
    [InlineData(NpmCliInstallState.MissingShimWithPackagePresent, NpmGlobalInstallMode.ForceRelink)]
    [InlineData(NpmCliInstallState.LauncherStubWithPackageAndShim, NpmGlobalInstallMode.Install)]
    public void Repair_plan_selects_remedy_for_package_and_shim_state(
        NpmCliInstallState state,
        NpmGlobalInstallMode expectedMode)
    {
        var plan = LocalCliRepairService.SelectRepairPlan(state);

        Assert.NotNull(plan);
        Assert.Equal(expectedMode, plan.InstallMode);
    }

    [Fact]
    public void Repair_plan_replays_package_postinstall_for_launcher_stub()
    {
        var plan = LocalCliRepairService.SelectRepairPlan(
            NpmCliInstallState.LauncherStubWithPackageAndShim);

        Assert.NotNull(plan);
        Assert.Equal(NpmCliRepairKind.PackagePostinstall, plan.Kind);
        Assert.Equal("launcher-stub-with-package-and-shim", plan.Detection);
    }

    [Theory]
    [InlineData(NpmCliInstallState.Unsupported)]
    [InlineData(NpmCliInstallState.PackagePresentWithShim)]
    public void Repair_plan_is_noop_for_unsupported_or_healthy_state(NpmCliInstallState state)
        => Assert.Null(LocalCliRepairService.SelectRepairPlan(state));

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
    public async Task Probe_installs_a_missing_package_once_and_then_is_a_noop()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        var shim = Path.Combine(npmBin, "codex.cmd");
        var installer = new FakeInstaller((_, mode) =>
        {
            Assert.Equal(NpmGlobalInstallMode.Install, mode);
            Directory.CreateDirectory(packageDir);
            File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
            File.WriteAllText(shim, "command shim");
        });
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));

        (bool Available, string? Version, string Path) Probe()
            => File.Exists(shim)
                ? (true, "codex-cli 0.151.0", shim)
                : (false, null, "codex");

        var repaired = await service.ProbeAndRepairAsync(
            "codex", null, Probe, CancellationToken.None);
        var healthy = await service.ProbeAndRepairAsync(
            "codex", "codex-cli 0.151.0", Probe, CancellationToken.None);

        Assert.True(repaired.Available);
        Assert.True(healthy.Available);
        Assert.True(Directory.Exists(packageDir));
        Assert.True(File.Exists(shim));
        Assert.Equal(1, installer.Calls);
        Assert.Equal(NpmGlobalInstallMode.Install, installer.LastMode);
    }

    [Fact]
    public async Task Probe_force_relinks_deleted_codex_shim_and_version_probe_succeeds()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(Path.Combine(packageDir, "bin"));
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        File.WriteAllText(Path.Combine(packageDir, "bin", "codex.js"), "package binary");
        var shim = Path.Combine(npmBin, "codex.cmd");
        File.WriteAllText(shim, "old command shim");
        File.Delete(shim);
        var installer = new FakeInstaller((_, mode) =>
        {
            Assert.Equal(NpmGlobalInstallMode.ForceRelink, mode);
            File.WriteAllText(shim, "restored command shim");
        });
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 8, 31, 11, 57, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));
        var versionProbeCalls = 0;

        (bool Available, string? Version, string Path) Probe()
        {
            versionProbeCalls++;
            return File.Exists(shim)
                ? (true, "codex-cli 0.151.0", shim)
                : (false, null, "codex");
        }

        var repaired = await service.ProbeAndRepairAsync(
            "codex", "0.151.0", Probe, CancellationToken.None);
        var healthy = await service.ProbeAndRepairAsync(
            "codex", repaired.Version, Probe, CancellationToken.None);

        Assert.True(File.Exists(shim));
        Assert.True(repaired.Available);
        Assert.Equal("codex-cli 0.151.0", repaired.Version);
        Assert.True(healthy.Available);
        Assert.Equal(1, installer.Calls);
        Assert.Equal(3, versionProbeCalls);
    }

    [Fact]
    public async Task Probe_repairs_claude_launcher_stub_with_package_postinstall()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        var nativePackage = Path.Combine(
            packageDir,
            "node_modules",
            "@anthropic-ai",
            "claude-code-win32-x64");
        var nativeBinary = Path.Combine(nativePackage, "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        Directory.CreateDirectory(nativePackage);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"2.1.263\"}");
        File.WriteAllText(Path.Combine(packageDir, "install.cjs"), "postinstall");
        File.WriteAllBytes(launcher, new byte[500]);
        File.WriteAllBytes(nativeBinary, new byte[8192]);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");
        var installer = new FakeInstaller(
            (_, _) => { },
            onPostinstall: (_, _, _) => File.Copy(nativeBinary, launcher, overwrite: true));
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 9, 6, 16, 32, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => new FileInfo(launcher).Length >= LocalCliRepairService.LauncherStubThresholdBytes
                ? (true, "2.1.263 (Claude Code)", "claude")
                : (false, "Error: claude native binary not installed.", "claude");

        var result = await service.ProbeAndRepairAsync(
            "claude", "2.1.261", Probe, CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(1, installer.PostinstallCalls);
        Assert.Equal(0, installer.Calls);
        Assert.Empty(service.Current());
        var journalText = File.ReadAllText(journal);
        Assert.Contains("launcher-stub-with-package-and-shim", journalText, StringComparison.Ordinal);
        Assert.Contains("\"launcherSizeBytes\":500", journalText, StringComparison.Ordinal);
        Assert.Contains("claude-code-win32-x64", journalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_package_postinstall_falls_back_to_exact_installed_version()
    {
        using var temp = new TempDirectory();
        var installer = new FallbackRecordingInstaller();

        var result = await installer.RunPackagePostinstallAsync(
            temp.Path,
            "@anthropic-ai/claude-code",
            "2.1.263",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("@anthropic-ai/claude-code@2.1.263", installer.PackageName);
        Assert.Equal(NpmGlobalInstallMode.Install, installer.Mode);
    }

    [Fact]
    public async Task Failed_launcher_stub_repair_is_projected_until_next_healthy_probe()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"2.1.263\"}");
        File.WriteAllText(Path.Combine(packageDir, "install.cjs"), "postinstall");
        File.WriteAllBytes(launcher, new byte[500]);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");
        var now = new DateTimeOffset(2026, 9, 6, 16, 32, 0, TimeSpan.Zero);
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }, NpmGlobalInstallOutcome.Failed),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => new FileInfo(launcher).Length >= LocalCliRepairService.LauncherStubThresholdBytes
                ? (true, "2.1.263 (Claude Code)", "claude")
                : (false, "Error: claude native binary not installed.", "claude");

        await service.ProbeAndRepairAsync("claude", "2.1.261", Probe, CancellationToken.None);

        var failure = Assert.Single(service.Current());
        Assert.Equal(nameof(NpmCliInstallState.LauncherStubWithPackageAndShim), failure.InstallState);
        Assert.Equal("launcher-stub-with-package-and-shim", failure.Detection);

        File.WriteAllBytes(launcher, new byte[8192]);
        now = now.AddMinutes(5);
        var healthy = await service.ProbeAndRepairAsync(
            "claude", "2.1.263", Probe, CancellationToken.None);

        Assert.True(healthy.Available);
        Assert.Empty(service.Current());
        Assert.Contains("\"outcome\":\"resolved\"", File.ReadAllText(journal), StringComparison.Ordinal);
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
        var installer = new FakeInstaller((_, _) => File.WriteAllText(shim, "shim"));
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
        Assert.True(File.Exists(shim));
        Assert.Equal(1, installer.Calls);
        Assert.Equal(NpmGlobalInstallMode.ForceRelink, installer.LastMode);
        Assert.Empty(service.Current());
        var journalText = File.ReadAllText(journal);
        Assert.Contains("missing-shim-with-package-present", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.231", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.234", journalText, StringComparison.Ordinal);
        Assert.Contains("npm update --global @anthropic-ai/claude-code", journalText, StringComparison.Ordinal);

        var healthyResult = await service.ProbeAndRepairAsync(
            "claude", "2.1.234", Probe, CancellationToken.None);
        Assert.True(healthyResult.Available);
        Assert.Equal(1, installer.Calls);

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
        Assert.Empty(restartedService.Current());
        await restartedService.ProbeAndRepairAsync(
            "claude", "2.1.234", Probe, CancellationToken.None);
        Assert.Equal(1, installer.Calls);
    }

    [Fact]
    public async Task Probe_reports_package_shim_and_relink_state_when_repair_fails()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        var loggerEntries = new List<string>();
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }),
            new CollectingLogger<LocalCliRepairService>(loggerEntries),
            () => new DateTimeOffset(2026, 8, 31, 11, 40, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));

        var result = await service.ProbeAndRepairAsync(
            "codex",
            "0.151.0",
            () => (false, null, "codex"),
            CancellationToken.None);

        Assert.False(result.Available);
        var status = Assert.Single(service.Current());
        Assert.Contains("package present", status.Detail, StringComparison.Ordinal);
        Assert.Contains("command shim absent", status.Detail, StringComparison.Ordinal);
        Assert.Contains("npm action force-relink attempted", status.Detail, StringComparison.Ordinal);
        Assert.Contains(loggerEntries, entry =>
            entry.Contains("packageStateBefore=present", StringComparison.Ordinal)
            && entry.Contains("shimStateBefore=absent", StringComparison.Ordinal)
            && entry.Contains("repairAction=force-relink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Healthy_probe_clears_out_of_band_repair_failure_and_restart_does_not_restore_it()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var now = new DateTimeOffset(2026, 8, 31, 11, 40, 0, TimeSpan.Zero);
        var available = false;
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }, NpmGlobalInstallOutcome.Failed),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => available
                ? (true, "codex-cli 0.151.0", Path.Combine(npmBin, "codex.cmd"))
                : (false, null, "codex");

        await service.ProbeAndRepairAsync("codex", "0.151.0", Probe, CancellationToken.None);
        Assert.Equal("failed", Assert.Single(service.Current()).Outcome);

        available = true;
        now = now.AddMinutes(5);
        var healthy = await service.ProbeAndRepairAsync(
            "codex",
            "0.151.0",
            Probe,
            CancellationToken.None);

        Assert.True(healthy.Available);
        Assert.Empty(service.Current());
        Assert.Contains("\"outcome\":\"resolved\"", File.ReadAllText(journal), StringComparison.Ordinal);

        var restartedService = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);
        Assert.Empty(restartedService.Current());
    }

    private sealed class FakeInstaller(
        Action<string, NpmGlobalInstallMode> onInstall,
        NpmGlobalInstallOutcome outcome = NpmGlobalInstallOutcome.Succeeded,
        Action<string, string, string?>? onPostinstall = null) : NpmGlobalInstaller
    {
        public int Calls { get; private set; }
        public int PostinstallCalls { get; private set; }
        public NpmGlobalInstallMode? LastMode { get; private set; }

        public override Task<NpmGlobalInstallResult> InstallAsync(
            string packageName,
            NpmGlobalInstallMode mode,
            CancellationToken ct)
        {
            Calls++;
            LastMode = mode;
            onInstall(packageName, mode);
            return Task.FromResult(new NpmGlobalInstallResult(
                outcome,
                outcome == NpmGlobalInstallOutcome.Succeeded ? 0 : 1,
                outcome == NpmGlobalInstallOutcome.Succeeded ? "installed" : "",
                outcome == NpmGlobalInstallOutcome.Succeeded ? "" : "install failed"));
        }

        public override Task<NpmGlobalInstallResult> RunPackagePostinstallAsync(
            string packageDirectory,
            string packageName,
            string? installedVersion,
            CancellationToken ct)
        {
            PostinstallCalls++;
            onPostinstall?.Invoke(packageDirectory, packageName, installedVersion);
            return Task.FromResult(new NpmGlobalInstallResult(
                outcome,
                outcome == NpmGlobalInstallOutcome.Succeeded ? 0 : 1,
                outcome == NpmGlobalInstallOutcome.Succeeded ? "postinstall complete" : "",
                outcome == NpmGlobalInstallOutcome.Succeeded ? "" : "postinstall failed"));
        }
    }

    private sealed class FallbackRecordingInstaller : NpmGlobalInstaller
    {
        public string? PackageName { get; private set; }
        public NpmGlobalInstallMode? Mode { get; private set; }

        public override Task<NpmGlobalInstallResult> InstallAsync(
            string packageName,
            NpmGlobalInstallMode mode,
            CancellationToken ct)
        {
            PackageName = packageName;
            Mode = mode;
            return Task.FromResult(new NpmGlobalInstallResult(
                NpmGlobalInstallOutcome.Succeeded,
                0,
                "installed",
                ""));
        }
    }

    private sealed class CollectingLogger<T>(List<string> entries) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(formatter(state, exception));
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
