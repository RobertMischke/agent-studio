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
    public async Task Installer_resolution_returns_an_absolute_npm_that_passes_version_preflight()
    {
        var resolution = await new NpmGlobalInstaller()
            .ResolveCommandAsync(CancellationToken.None);

        var command = Assert.IsType<NpmCommand>(resolution.Command);
        Assert.True(Path.IsPathFullyQualified(command.NpmPath));
        Assert.True(File.Exists(command.NpmPath));
        Assert.False(string.IsNullOrWhiteSpace(resolution.Version));
    }

    [Fact]
    public async Task Installer_reports_typed_npm_unavailable_when_no_candidate_exists()
    {
        using var temp = new TempDirectory();
        var installer = new NpmGlobalInstaller(
            name => name == "PATH" ? temp.Path : null,
            () => false);

        var result = await installer.InstallAsync(
            "@openai/codex",
            NpmGlobalInstallMode.ForceRelink,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(NpmGlobalInstallFailureKind.NpmUnavailable, result.FailureKind);
        Assert.Equal("npm-unavailable", result.Outcome);
        Assert.Contains("no npm executable candidate", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Installer_preflight_classifies_a_broken_npm_without_running_install()
    {
        using var temp = new TempDirectory();
        var npmPath = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                npmPath,
                "@echo Error: Cannot find module 'broken/npm-cli.js' 1>&2\r\n@exit /b 1\r\n");
        }
        else
        {
            File.WriteAllText(
                npmPath,
                "#!/bin/sh\necho \"Error: Cannot find module broken/npm-cli.js\" >&2\nexit 1\n");
            File.SetUnixFileMode(
                npmPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var installer = new NpmGlobalInstaller(
            name => name switch
            {
                "PATH" => temp.Path,
                "ComSpec" or "SystemRoot" => Environment.GetEnvironmentVariable(name),
                _ => null,
            },
            OperatingSystem.IsWindows);

        var result = await installer.InstallAsync(
            "@openai/codex",
            NpmGlobalInstallMode.ForceRelink,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(NpmGlobalInstallFailureKind.NpmUnavailable, result.FailureKind);
        Assert.Equal("npm-unavailable", result.Outcome);
        Assert.StartsWith(
            "npm unavailable: no candidate passed 'npm --version'",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("    at ", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NpmCliInstallState.TrulyUninstalled, NpmGlobalInstallMode.Install)]
    [InlineData(NpmCliInstallState.MissingShimWithPackagePresent, NpmGlobalInstallMode.ForceRelink)]
    public void Repair_plan_selects_remedy_for_package_and_shim_state(
        NpmCliInstallState state,
        NpmGlobalInstallMode expectedMode)
    {
        var plan = LocalCliRepairService.SelectRepairPlan(state);

        Assert.NotNull(plan);
        Assert.Equal(expectedMode, plan.InstallMode);
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
    public async Task Healthy_probe_clears_out_of_band_resolved_failure_and_restart_does_not_restore_it()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var packageDir = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var now = new DateTimeOffset(2026, 8, 31, 11, 40, 0, TimeSpan.Zero);
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);
        var healthy = false;

        (bool Available, string? Version, string Path) Probe()
            => healthy
                ? (true, "codex-cli 0.151.0", Path.Combine(appData, "npm", "codex.cmd"))
                : (false, null, "codex");

        await service.ProbeAndRepairAsync("codex", "0.151.0", Probe, CancellationToken.None);
        Assert.Equal("failed", Assert.Single(service.Current()).Outcome);

        healthy = true;
        now = now.AddMinutes(5);
        var recovered = await service.ProbeAndRepairAsync(
            "codex", "0.151.0", Probe, CancellationToken.None);

        Assert.True(recovered.Available);
        Assert.Empty(service.Current());
        Assert.Contains("\"outcome\":\"healthy\"", File.ReadAllText(journal), StringComparison.Ordinal);

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

    private sealed class FakeInstaller(Action<string, NpmGlobalInstallMode> onInstall) : NpmGlobalInstaller
    {
        public int Calls { get; private set; }
        public NpmGlobalInstallMode? LastMode { get; private set; }

        public override Task<NpmGlobalInstallResult> InstallAsync(
            string packageName,
            NpmGlobalInstallMode mode,
            CancellationToken ct)
        {
            Calls++;
            LastMode = mode;
            onInstall(packageName, mode);
            return Task.FromResult(new NpmGlobalInstallResult(true, 0, "installed", ""));
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
