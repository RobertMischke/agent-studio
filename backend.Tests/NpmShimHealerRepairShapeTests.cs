using AgentStudio.Cli;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Behavioral coverage for the AGT-2673 self-heal extension to
/// <see cref="NpmShimHealer"/>: classifying which repair shape a broken
/// claude npm-shim install is in, and the bounded-to-one-per-hour reinstall
/// journal. Both pieces are deliberately pure/file-scoped so they run on any
/// OS without spawning processes or gating on
/// <see cref="OperatingSystem.IsWindows"/> - unlike
/// <see cref="NpmShimHealer.TryHealClaudeAsync"/> itself, which is
/// Windows-only and has no behavioral test (see
/// <c>LegacyNpmShimRepairContractTests</c> for its wiring-only pin).
/// </summary>
public sealed class NpmShimHealerRepairShapeTests
{
    // ===== ClassifyShimState: pure decision matrix =====

    [Fact]
    public void LauncherPresent_IsHealthy_RegardlessOfPackage()
    {
        Assert.Equal(ClaudeShimRepairShape.Healthy,
            NpmShimHealer.ClassifyShimState(launcherPresent: true, packageDirPresent: true));
        Assert.Equal(ClaudeShimRepairShape.Healthy,
            NpmShimHealer.ClassifyShimState(launcherPresent: true, packageDirPresent: false));
    }

    [Fact]
    public void LauncherMissing_PackagePresent_IsMissingLauncherPackagePresent()
    {
        Assert.Equal(ClaudeShimRepairShape.MissingLauncherPackagePresent,
            NpmShimHealer.ClassifyShimState(launcherPresent: false, packageDirPresent: true));
    }

    [Fact]
    public void LauncherMissing_PackageMissing_IsUninstalled()
    {
        // The distinguishing case the card asked for: no package directory
        // at all means a first-time install, not a repair candidate.
        Assert.Equal(ClaudeShimRepairShape.Uninstalled,
            NpmShimHealer.ClassifyShimState(launcherPresent: false, packageDirPresent: false));
    }

    // ===== IsInCooldown pure overload: no filesystem, no clock =====

    [Fact]
    public void PureIsInCooldown_NoLastAttempt_ReturnsFalse()
    {
        Assert.False(NpmReinstallJournal.IsInCooldown(null, DateTime.UtcNow, out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void PureIsInCooldown_WithinHour_ReturnsTrueWithExactRemaining()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(-20);

        Assert.True(NpmReinstallJournal.IsInCooldown(last, now, out var remaining));
        Assert.Equal(TimeSpan.FromMinutes(40), remaining);
    }

    [Fact]
    public void PureIsInCooldown_ExactlyOneHour_ReturnsFalse()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddHours(-1);

        Assert.False(NpmReinstallJournal.IsInCooldown(last, now, out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    // ===== TryReadPackageVersion: "before" version source for the shape
    // where the launcher (and therefore --version) is unavailable =====

    [Fact]
    public void TryReadPackageVersion_ValidPackageJson_ReturnsVersion()
    {
        var wrapDir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(wrapDir, "package.json"),
                "{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"2.1.234\"}");

            Assert.Equal("2.1.234", NpmShimHealer.TryReadPackageVersion(wrapDir));
        }
        finally { Cleanup(wrapDir); }
    }

    [Fact]
    public void TryReadPackageVersion_MissingFile_ReturnsNull()
    {
        var wrapDir = MakeTempDir();
        try
        {
            Assert.Null(NpmShimHealer.TryReadPackageVersion(wrapDir));
        }
        finally { Cleanup(wrapDir); }
    }

    [Fact]
    public void TryReadPackageVersion_MalformedJson_ReturnsNull()
    {
        var wrapDir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(wrapDir, "package.json"), "{not json");

            Assert.Null(NpmShimHealer.TryReadPackageVersion(wrapDir));
        }
        finally { Cleanup(wrapDir); }
    }

    // ===== NpmReinstallJournal: cooldown + append =====

    [Fact]
    public void IsInCooldown_NoJournal_ReturnsFalse()
    {
        var npmBin = MakeTempDir();
        try
        {
            Assert.False(NpmReinstallJournal.IsInCooldown(npmBin, out var remaining));
            Assert.Equal(TimeSpan.Zero, remaining);
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void IsInCooldown_RecentAttempt_ReturnsTrueWithRemainingBudget()
    {
        var npmBin = MakeTempDir();
        try
        {
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddMinutes(-10), "missing-launcher-package-present", "2.1.231", "2.1.234", "repaired"));

            var inCooldown = NpmReinstallJournal.IsInCooldown(npmBin, out var remaining);

            Assert.True(inCooldown);
            // 60m window minus 10m elapsed leaves ~50m; allow scheduling slack.
            Assert.InRange(remaining, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(51));
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void IsInCooldown_AttemptOverAnHourAgo_ReturnsFalse()
    {
        var npmBin = MakeTempDir();
        try
        {
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddHours(-2), "missing-launcher-package-present", null, "2.1.234", "repaired"));

            Assert.False(NpmReinstallJournal.IsInCooldown(npmBin, out var remaining));
            Assert.Equal(TimeSpan.Zero, remaining);
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void IsInCooldown_UsesMostRecentOfMultipleEntries()
    {
        var npmBin = MakeTempDir();
        try
        {
            // First a stale attempt, then a fresh one - cooldown must key off
            // the latest, not the first, journal line.
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddHours(-5), "missing-launcher-package-present", null, "2.1.231", "failed"));
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddMinutes(-1), "missing-launcher-package-present", "2.1.231", "2.1.234", "repaired"));

            Assert.True(NpmReinstallJournal.IsInCooldown(npmBin, out var remaining));
            Assert.True(remaining > TimeSpan.FromMinutes(58));
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void Append_IsIdempotentAcrossCallers_AllEntriesReadable()
    {
        // Root-cause capture requires every attempt to survive, not just the
        // last one - both the boot-time shell preflight and the in-process
        // healer append to the same file across a host's lifetime.
        var npmBin = MakeTempDir();
        try
        {
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddHours(-3), "missing-launcher-package-present", "2.1.230", "2.1.231", "repaired"));
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow.AddMinutes(-30), "missing-launcher-package-present", "2.1.231", "2.1.234", "repaired"));

            var journalPath = Directory.GetFiles(npmBin, ".atp-npm-reinstall-journal.jsonl").Single();
            var lines = File.ReadAllLines(journalPath);

            Assert.Equal(2, lines.Length);
            Assert.Contains("2.1.230", lines[0]);
            Assert.Contains("2.1.234", lines[1]);
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void IsInCooldown_ReadsShellWrittenJournalLine()
    {
        // tools/check-cli-shims.sh (the bash sibling) hand-writes journal
        // lines with plain lowercase keys via printf, not System.Text.Json.
        // Both tools append to and read the same file, so the C# side must
        // tolerate that exact shape or the shared cooldown silently breaks
        // when the two entry points interleave on one host.
        var npmBin = MakeTempDir();
        try
        {
            var shellStyleLine =
                "{\"ts\":\"" + DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"," +
                "\"trigger\":\"missing-launcher-package-present\"," +
                "\"versionBefore\":null,\"versionAfter\":\"2.1.234 (Claude Code)\",\"outcome\":\"repaired\"}";
            File.WriteAllText(
                Path.Combine(npmBin, ".atp-npm-reinstall-journal.jsonl"), shellStyleLine + "\n");

            Assert.True(NpmReinstallJournal.IsInCooldown(npmBin, out var remaining));
            Assert.True(remaining > TimeSpan.FromMinutes(50));
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void Append_WritesCamelCaseKeys_MatchingShellFormat()
    {
        var npmBin = MakeTempDir();
        try
        {
            NpmReinstallJournal.Append(npmBin, new NpmReinstallJournalEntry(
                DateTime.UtcNow, "missing-launcher-package-present", "2.1.231", "2.1.234", "repaired"));

            var line = File.ReadAllText(Path.Combine(npmBin, ".atp-npm-reinstall-journal.jsonl"));

            Assert.Contains("\"ts\":", line);
            Assert.Contains("\"trigger\":", line);
            Assert.Contains("\"versionBefore\":", line);
            Assert.Contains("\"versionAfter\":", line);
            Assert.Contains("\"outcome\":", line);
            Assert.DoesNotContain("\"Ts\":", line);
        }
        finally { Cleanup(npmBin); }
    }

    [Fact]
    public void IsInCooldown_MalformedJournal_TreatedAsNoHistory()
    {
        // A corrupt journal must never permanently block repair.
        var npmBin = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(npmBin, ".atp-npm-reinstall-journal.jsonl"), "not json\n{\"broken\n");

            Assert.False(NpmReinstallJournal.IsInCooldown(npmBin, out var remaining));
            Assert.Equal(TimeSpan.Zero, remaining);
        }
        finally { Cleanup(npmBin); }
    }

    // ===== helpers =====

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-npm-journal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
