using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the AGT-2673 shim-detection and repair contract for
/// <see cref="NpmShimHealer"/>: distinguishing a fully-missing npm bin-link
/// with the package still present (auto-repairable via <c>npm install -g</c>)
/// from a genuinely uninstalled package (no automatic repair), the hourly
/// throttle on the reinstall attempt, and the journal written to
/// <c>logs/npm-shim-repairs.jsonl</c>.
///
/// <para>
/// The tests drive <see cref="NpmShimHealer.HealAtAsync"/> directly against a
/// synthetic <c>npmBin</c> directory tree instead of going through
/// <see cref="NpmShimHealer.TryHealClaudeAsync"/>, because the public entry
/// point is gated on <c>OperatingSystem.IsWindows()</c> and real
/// <c>APPDATA</c> - the underlying file checks have no actual Windows-API
/// dependency, so the decision logic stays testable on any platform.
/// </para>
/// </summary>
public sealed class NpmShimHealerTests : IDisposable
{
    private readonly string _npmBin;
    private readonly string _workspaceRoot;

    public NpmShimHealerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "atp-npm-shim-healer-" + Guid.NewGuid().ToString("N"));
        _npmBin = Path.Combine(root, "npm");
        _workspaceRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(_npmBin);
        Directory.CreateDirectory(_workspaceRoot);
        NpmShimHealer.ResetNpmInstallThrottleForTests();
    }

    public void Dispose()
    {
        NpmShimHealer.ResetNpmInstallThrottleForTests();
        try { Directory.Delete(Path.GetDirectoryName(_npmBin)!, recursive: true); } catch { /* best-effort */ }
    }

    private string WrapDir => Path.Combine(_npmBin, "node_modules", "@anthropic-ai", "claude-code");
    private string Shim => Path.Combine(_npmBin, "claude.cmd");
    private string JournalPath => Path.Combine(_workspaceRoot, "logs", "npm-shim-repairs.jsonl");

    private void CreatePackage(string version)
    {
        Directory.CreateDirectory(WrapDir);
        File.WriteAllText(Path.Combine(WrapDir, "package.json"), $"{{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"{version}\"}}");
        // A plausible, non-stub wrapper binary so step 3 (postinstall repair)
        // does not fire and mask the scenario under test - only the
        // top-level npm bin-link (claude.cmd) is missing.
        Directory.CreateDirectory(Path.Combine(WrapDir, "bin"));
        File.WriteAllBytes(Path.Combine(WrapDir, "bin", "claude.exe"), new byte[8192]);
    }

    private static Func<ILogger, CancellationToken, Task<(bool Ok, string? Output, string? Error)>> FakeNpmInstall(bool ok, string? error = null)
        => (_, _) => Task.FromResult((ok, ok ? "+ @anthropic-ai/claude-code@2.1.234" : null, error));

    // ===== Diagnosis: truly uninstalled =====

    [Fact]
    public async Task ShimMissing_NoPackageDirectory_DiagnosesTrulyUninstalled_NoInstallAttempted()
    {
        var outcome = await NpmShimHealer.HealAtAsync(_npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot);

        Assert.False(outcome.Available);
        Assert.Equal(NpmShimHealDiagnosis.TrulyUninstalled, outcome.Diagnosis);
        Assert.False(outcome.NpmInstallAttempted);
        Assert.False(outcome.NpmInstallThrottled);
        Assert.Contains("not found", outcome.Error);
    }

    // ===== Diagnosis: shim missing but package present =====

    [Fact]
    public async Task ShimMissing_PackagePresent_AttemptsNpmInstall()
    {
        CreatePackage("2.1.231");

        var outcome = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            npmInstallRunner: FakeNpmInstall(ok: false, error: "network unreachable"));

        Assert.Equal(NpmShimHealDiagnosis.ShimMissingPackagePresent, outcome.Diagnosis);
        Assert.True(outcome.NpmInstallAttempted);
        Assert.False(outcome.NpmInstallThrottled);
        Assert.False(outcome.Available);
        Assert.Equal("2.1.231", outcome.VersionBefore);
        Assert.Contains("network unreachable", outcome.Error);
    }

    [Fact]
    public async Task ShimMissing_PackagePresent_NpmInstallSucceeds_ShimStillMissing_ReportsUnavailable()
    {
        // npm install "succeeds" per the fake runner but never actually created
        // claude.cmd on disk (a real npm failure mode - install exits 0 while
        // bin-linking silently no-ops, e.g. a permissions issue) - the healer
        // must not claim success just because the child process exited clean.
        CreatePackage("2.1.231");

        var outcome = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            npmInstallRunner: FakeNpmInstall(ok: true));

        Assert.True(outcome.NpmInstallAttempted);
        Assert.False(outcome.Available);
        Assert.Contains("still missing after npm install", outcome.Error);
    }

    [Fact]
    public async Task ShimMissing_PackagePresent_NpmInstallSucceeds_ShimAppears_ReportsAvailable()
    {
        CreatePackage("2.1.234");

        var outcome = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            npmInstallRunner: (_, _) =>
            {
                // Simulate what a real `npm install -g` does: it creates the
                // bin-link. Use an executable script so the post-install smoke
                // test (`claude.cmd --version`) can actually run.
                WriteExecutableVersionScript(Shim, "2.1.234 (Claude Code)");
                return Task.FromResult((true, (string?)"installed", (string?)null));
            });

        Assert.True(outcome.Available);
        Assert.True(outcome.NpmInstallAttempted);
        Assert.Equal(NpmShimHealDiagnosis.ShimMissingPackagePresent, outcome.Diagnosis);
        Assert.Equal("2.1.234", outcome.VersionBefore);
        Assert.Equal("2.1.234 (Claude Code)", outcome.VersionAfter);
    }

    // ===== Hourly throttle =====

    [Fact]
    public async Task ShimMissing_SecondAttemptWithinCooldown_IsThrottled()
    {
        CreatePackage("2.1.231");
        var t0 = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

        var first = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot, utcNow: t0,
            npmInstallRunner: FakeNpmInstall(ok: false, error: "boom"));
        Assert.True(first.NpmInstallAttempted);
        Assert.False(first.NpmInstallThrottled);

        var attemptedSecondTime = false;
        var second = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            utcNow: t0.AddMinutes(30),
            npmInstallRunner: (_, _) => { attemptedSecondTime = true; return Task.FromResult((false, (string?)null, (string?)"should not run")); });

        Assert.True(second.NpmInstallThrottled);
        Assert.False(second.NpmInstallAttempted);
        Assert.False(attemptedSecondTime);
    }

    [Fact]
    public async Task ShimMissing_AttemptAfterCooldownElapsed_RetriesInstall()
    {
        CreatePackage("2.1.231");
        var t0 = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);

        await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot, utcNow: t0,
            npmInstallRunner: FakeNpmInstall(ok: false, error: "boom"));

        var attemptedSecondTime = false;
        var second = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            utcNow: t0.Add(NpmShimHealer.NpmInstallCooldown).AddSeconds(1),
            npmInstallRunner: (_, _) => { attemptedSecondTime = true; return Task.FromResult((false, (string?)null, (string?)"boom again")); });

        Assert.True(attemptedSecondTime);
        Assert.True(second.NpmInstallAttempted);
        Assert.False(second.NpmInstallThrottled);
    }

    // ===== Orphan-rename path unaffected (pre-existing behavior) =====

    [Fact]
    public async Task OrphanShim_RenamedBackAndSmokeTested_NoNpmInstallNeeded()
    {
        // The atomic-rename orphan shape: npm wrote `.claude.cmd-<random>` and
        // the final rename to `claude.cmd` never completed. This is
        // recoverable by step 1 alone - no npm install -g should ever fire.
        CreatePackage("2.1.234");
        var orphan = Path.Combine(_npmBin, ".claude.cmd-a1b2c3");
        WriteExecutableVersionScript(orphan, "2.1.234 (Claude Code)");

        var attempted = false;
        var outcome = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            npmInstallRunner: (_, _) => { attempted = true; return Task.FromResult((false, (string?)null, (string?)"must not run")); });

        Assert.False(attempted);
        Assert.True(outcome.Available);
        Assert.Equal(NpmShimHealDiagnosis.RepairedWithoutReinstall, outcome.Diagnosis);
        Assert.Contains(outcome.Actions, a => a.Contains("renamed orphan shim"));
        Assert.Equal("2.1.234 (Claude Code)", outcome.VersionAfter);
        Assert.True(File.Exists(Shim));
        Assert.False(File.Exists(orphan));
    }

    // ===== Journal =====

    [Fact]
    public async Task Repair_WritesNpmShimRepairsJsonlRow()
    {
        CreatePackage("2.1.231");

        await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, _workspaceRoot,
            npmInstallRunner: FakeNpmInstall(ok: false, error: "network unreachable"));

        Assert.True(File.Exists(JournalPath));
        var lines = File.ReadAllLines(JournalPath).Where(l => l.Length > 0).ToList();
        Assert.Single(lines);
        var row = lines[0];
        Assert.Contains("\"cliType\":\"claude\"", row);
        Assert.Contains("\"diagnosis\":\"shim-missing-package-present\"", row);
        Assert.Contains("\"available\":false", row);
        Assert.Contains("\"npmInstallAttempted\":true", row);
        Assert.Contains("\"versionBefore\":\"2.1.231\"", row);
    }

    [Fact]
    public async Task Repair_WithNoWorkspaceRoot_SkipsJournalWithoutThrowing()
    {
        var outcome = await NpmShimHealer.HealAtAsync(
            _npmBin, NullLogger.Instance, CancellationToken.None, workspaceRoot: null);

        Assert.Equal(NpmShimHealDiagnosis.TrulyUninstalled, outcome.Diagnosis);
        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "logs")));
    }

    // ===== Helpers =====

    private static void WriteExecutableVersionScript(string path, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"#!/bin/sh\necho '{version}'\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
