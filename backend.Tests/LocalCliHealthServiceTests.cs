using System.Text.Json;

using AgentStudio.HostHealth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The repair coordinator: what it repairs, what it refuses to repair, how
/// often it is willing to try, and what it leaves behind for the operator.
/// Uses a fake npm installer and a fake clock, so nothing here installs
/// anything or depends on wall-clock time.
/// </summary>
public class LocalCliHealthServiceTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "agt-2673-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _npmRoot;
    private DateTime _now = new(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

    public LocalCliHealthServiceTests()
    {
        _npmRoot = Path.Combine(_workspace, "npm");
        Directory.CreateDirectory(Path.Combine(_npmRoot, "bin"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        GC.SuppressFinalize(this);
    }

    // ===== Fakes =====

    private sealed class FakeProbe : ILocalCliVersionProbe
    {
        public bool Available { get; set; }
        public string? Version { get; set; }
        public (bool Available, string? Version) Probe(string cliType) => (Available, Version);
    }

    private sealed class FakeInstaller : IGlobalNpmPackageInstaller
    {
        private readonly Action<FakeInstaller> _onInstall;
        public FakeInstaller(Action<FakeInstaller> onInstall) => _onInstall = onInstall;

        public List<string> Installed { get; } = [];
        public bool Succeeds { get; set; } = true;

        public Task<GlobalNpmInstallResult> InstallGlobalAsync(string packageId, CancellationToken ct)
        {
            Installed.Add(packageId);
            _onInstall(this);
            return Task.FromResult(Succeeds
                ? new GlobalNpmInstallResult(true, 0, 1234, "added 1 package", null)
                : new GlobalNpmInstallResult(false, 1, 1234, "npm ERR! code EACCES", "npm exited 1"));
        }
    }

    // ===== Scenarios =====

    [Fact]
    public async Task The_missing_shim_shape_is_repaired_and_the_versions_are_journalled()
    {
        // The observed control-plane breakage: package on disk at 2.1.231,
        // shims gone. The reinstall puts the shims back at 2.1.234.
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var installer = new FakeInstaller(_ =>
        {
            WriteShim();
            WritePackage("2.1.234");
            probe.Available = true;
            probe.Version = "2.1.234 (Claude Code)";
        });
        var service = Build(probe, installer);

        var entry = await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Equal(["@anthropic-ai/claude-code"], installer.Installed);
        Assert.Equal(nameof(LocalCliInstallState.Ready), entry.State);
        Assert.True(entry.Available);

        var row = ReadSingleJournalRow();
        Assert.True(row.GetProperty("attempted").GetBoolean());
        Assert.True(row.GetProperty("repaired").GetBoolean());
        Assert.Equal(nameof(LocalCliInstallState.ShimMissingPackagePresent), row.GetProperty("state").GetString());
        Assert.Equal("2.1.231", row.GetProperty("packageVersionBefore").GetString());
        Assert.Equal("2.1.234", row.GetProperty("packageVersionAfter").GetString());
        Assert.Equal("2.1.234 (Claude Code)", row.GetProperty("versionAfter").GetString());
    }

    [Fact]
    public async Task A_successful_repair_leaves_a_note_for_the_status_bar()
    {
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var service = Build(probe, new FakeInstaller(_ => { WriteShim(); probe.Available = true; probe.Version = "2.1.234"; }));

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        var note = Assert.Single(service.RecentNotes());
        Assert.True(note.Repaired);
        Assert.Equal(_now, note.At);
        Assert.Contains("claude CLI repaired", note.Message, StringComparison.Ordinal);
        Assert.Contains("bin shims were missing", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_uninstalled_cli_is_never_installed_automatically()
    {
        var probe = new FakeProbe { Available = false };
        var installer = new FakeInstaller(_ => throw new InvalidOperationException("must not install"));
        var service = Build(probe, installer);

        var entry = await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Empty(installer.Installed);
        Assert.Equal(nameof(LocalCliInstallState.NotInstalled), entry.State);
        Assert.Equal(nameof(LocalCliRepairAction.EscalateToOperator), entry.Action);
    }

    [Fact]
    public async Task A_second_automatic_attempt_inside_the_window_is_suppressed()
    {
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var installer = new FakeInstaller(_ => { /* repair does not stick: the host stays broken */ });
        var service = Build(probe, installer);

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        _now = _now.AddMinutes(20);
        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Single(installer.Installed);
    }

    [Fact]
    public async Task The_next_window_allows_another_automatic_attempt()
    {
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var installer = new FakeInstaller(_ => { });
        var service = Build(probe, installer);

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        _now = _now.AddHours(1);
        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Equal(2, installer.Installed.Count);
    }

    [Fact]
    public async Task An_operator_request_repairs_even_inside_the_window()
    {
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var installer = new FakeInstaller(_ => { });
        var service = Build(probe, installer);

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        _now = _now.AddMinutes(1);
        await service.EnsureHealthyAsync("claude", operatorRequested: true, CancellationToken.None);

        Assert.Equal(2, installer.Installed.Count);
    }

    [Fact]
    public async Task A_failed_repair_is_journalled_with_its_error_and_flagged_in_the_note()
    {
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var service = Build(probe, new FakeInstaller(_ => { }) { Succeeds = false });

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        var row = ReadSingleJournalRow();
        Assert.True(row.GetProperty("attempted").GetBoolean());
        Assert.False(row.GetProperty("repaired").GetBoolean());
        Assert.Equal("npm exited 1", row.GetProperty("error").GetString());

        var note = Assert.Single(service.RecentNotes());
        Assert.False(note.Repaired);
        Assert.Contains("repair failed", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_steady_unhealthy_state_does_not_append_a_row_on_every_probe()
    {
        var probe = new FakeProbe { Available = false };
        var service = Build(probe, new FakeInstaller(_ => { }));

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        _now = _now.AddMinutes(5);
        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Single(JournalLines());
    }

    [Fact]
    public async Task A_host_that_stays_broken_inside_the_window_journals_one_throttled_row()
    {
        // Five-minute probe, one-hour window: without dedupe this would append
        // eleven identical rows an hour while deliberately doing nothing.
        var probe = new FakeProbe { Available = false };
        WritePackage("2.1.231");
        var service = Build(probe, new FakeInstaller(_ => { }));

        await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        for (var tick = 1; tick <= 3; tick++)
        {
            _now = _now.AddMinutes(5);
            await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);
        }

        var lines = JournalLines();
        Assert.Equal(2, lines.Length);
        var throttled = JsonDocument.Parse(lines[1]).RootElement;
        Assert.False(throttled.GetProperty("attempted").GetBoolean());
        Assert.Contains("next attempt in", throttled.GetProperty("throttledReason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healthy_cli_writes_nothing_and_reports_ready()
    {
        var probe = new FakeProbe { Available = true, Version = "2.1.234" };
        var service = Build(probe, new FakeInstaller(_ => throw new InvalidOperationException("must not install")));

        var entry = await service.EnsureHealthyAsync("claude", operatorRequested: false, CancellationToken.None);

        Assert.Equal(nameof(LocalCliInstallState.Ready), entry.State);
        Assert.Empty(JournalLines());
    }

    [Fact]
    public async Task An_unknown_cli_type_is_rejected_at_the_boundary()
    {
        var service = Build(new FakeProbe(), new FakeInstaller(_ => throw new InvalidOperationException("must not install")));

        var entry = await service.EnsureHealthyAsync("gemini", operatorRequested: true, CancellationToken.None);

        Assert.Equal(nameof(LocalCliInstallState.Unknown), entry.State);
        Assert.Equal(nameof(LocalCliRepairAction.EscalateToOperator), entry.Action);
    }

    [Fact]
    public void Inspect_reports_every_known_cli_without_repairing()
    {
        var installer = new FakeInstaller(_ => throw new InvalidOperationException("must not install"));
        var snapshot = Build(new FakeProbe(), installer).Inspect();

        Assert.Equal(["claude", "codex"], snapshot.Clis.Select(cli => cli.CliType));
        Assert.Empty(installer.Installed);
    }

    // ===== Wiring =====

    private LocalCliHealthService Build(ILocalCliVersionProbe probe, IGlobalNpmPackageInstaller installer)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();

        var inspector = new LocalCliInstallInspector(
            NullLogger<LocalCliInstallInspector>.Instance,
            new NpmGlobalLayout(
                Path.Combine(_npmRoot, "bin"),
                Path.Combine(_npmRoot, "node_modules"),
                Path.Combine(_npmRoot, "_logs")),
            isWindows: false);

        return new LocalCliHealthService(
            probe,
            inspector,
            installer,
            new LocalCliRepairJournal(configuration, NullLogger<LocalCliRepairJournal>.Instance),
            NullLogger<LocalCliHealthService>.Instance,
            LocalCliRepairThrottle.DefaultWindow,
            () => _now);
    }

    private void WriteShim()
        => File.WriteAllText(Path.Combine(_npmRoot, "bin", "claude"), "#!/bin/sh");

    private void WritePackage(string version)
    {
        var directory = Path.Combine(_npmRoot, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            $$"""{"name":"@anthropic-ai/claude-code","version":"{{version}}"}""");
    }

    private string[] JournalLines()
    {
        var path = Path.Combine(_workspace, "logs", "cli-repairs.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private JsonElement ReadSingleJournalRow()
        => JsonDocument.Parse(Assert.Single(JournalLines())).RootElement;
}
