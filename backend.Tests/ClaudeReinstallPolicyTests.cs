using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2673: the pure gate for the <c>NpmShimHealer</c> full-reinstall
/// fallback (truly-uninstalled vs cooldown vs go), plus the durable journal
/// it reads/writes. The caller only ever calls <c>Decide</c> from inside its
/// own "shim is missing" check, so there is no "shim present" case to test
/// here. Kept as a direct matrix test per docs/quality/dotnet-backend.md's
/// "pure policy first" - no filesystem, process, or clock setup for the
/// decision itself.
/// </summary>
public sealed class ClaudeReinstallPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_package_on_disk_is_truly_uninstalled_and_never_attempted()
    {
        var decision = ClaudeReinstallPolicy.Decide(packagePresent: false, lastAttemptAt: null, now: Now);

        Assert.Equal(ClaudeReinstallDecision.TrulyUninstalled, decision);
    }

    [Fact]
    public void Package_present_with_no_prior_attempt_is_go()
    {
        var decision = ClaudeReinstallPolicy.Decide(packagePresent: true, lastAttemptAt: null, now: Now);

        Assert.Equal(ClaudeReinstallDecision.Attempt, decision);
    }

    [Fact]
    public void An_attempt_thirty_minutes_ago_is_inside_the_cooldown()
    {
        var decision = ClaudeReinstallPolicy.Decide(
            packagePresent: true,
            lastAttemptAt: Now - TimeSpan.FromMinutes(30),
            now: Now);

        Assert.Equal(ClaudeReinstallDecision.CooldownActive, decision);
    }

    [Fact]
    public void An_attempt_exactly_at_the_cooldown_boundary_is_elapsed()
    {
        var decision = ClaudeReinstallPolicy.Decide(
            packagePresent: true,
            lastAttemptAt: Now - ClaudeReinstallPolicy.Cooldown,
            now: Now);

        Assert.Equal(ClaudeReinstallDecision.Attempt, decision);
    }

    [Fact]
    public void An_attempt_one_second_short_of_the_cooldown_boundary_is_still_active()
    {
        var decision = ClaudeReinstallPolicy.Decide(
            packagePresent: true,
            lastAttemptAt: Now - ClaudeReinstallPolicy.Cooldown + TimeSpan.FromSeconds(1),
            now: Now);

        Assert.Equal(ClaudeReinstallDecision.CooldownActive, decision);
    }

    [Fact]
    public void A_custom_cooldown_overrides_the_default()
    {
        var decision = ClaudeReinstallPolicy.Decide(
            packagePresent: true,
            lastAttemptAt: Now - TimeSpan.FromMinutes(10),
            now: Now,
            cooldown: TimeSpan.FromMinutes(5));

        Assert.Equal(ClaudeReinstallDecision.Attempt, decision);
    }
}

/// <summary>Round-trips through a real temp directory; no process ever spawns.</summary>
public sealed class ClaudeRepairJournalStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("claude-repair-journal-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) { AgentStudio.Diagnostics.SilentCatch.Note(ex, "ClaudeRepairJournalStoreTests cleanup"); }
    }

    [Fact]
    public void No_file_yet_reads_as_empty_and_last_attempt_is_null()
    {
        Assert.Empty(ClaudeRepairJournalStore.TryReadEntries(_dir));
        Assert.Null(ClaudeRepairJournalStore.TryReadLastAttemptAt(_dir));
    }

    [Fact]
    public void Appended_entry_round_trips_with_version_before_and_after()
    {
        var entry = new ClaudeRepairJournalEntry(
            AttemptedAt: DateTimeOffset.Parse("2026-08-20T09:05:00Z"),
            Succeeded: true,
            VersionBefore: "2.1.231",
            VersionAfter: "2.1.234",
            Detail: "CLI repaired at 2026-08-20T09:05:00Z via npm install -g reinstall (version 2.1.231 -> 2.1.234)");

        ClaudeRepairJournalStore.Append(_dir, entry);
        var entries = ClaudeRepairJournalStore.TryReadEntries(_dir);

        Assert.Single(entries);
        Assert.Equal(entry, entries[0]);
        Assert.Equal(entry.AttemptedAt, ClaudeRepairJournalStore.TryReadLastAttemptAt(_dir));
    }

    [Fact]
    public void Last_attempt_at_reflects_the_most_recent_of_several_entries()
    {
        var first = new ClaudeRepairJournalEntry(
            DateTimeOffset.Parse("2026-08-13T09:00:00Z"), true, "2.1.220", "2.1.231", "first repair");
        var second = new ClaudeRepairJournalEntry(
            DateTimeOffset.Parse("2026-08-18T09:00:00Z"), true, "2.1.231", "2.1.234", "second repair");

        ClaudeRepairJournalStore.Append(_dir, first);
        ClaudeRepairJournalStore.Append(_dir, second);

        Assert.Equal(second.AttemptedAt, ClaudeRepairJournalStore.TryReadLastAttemptAt(_dir));
        Assert.Equal(2, ClaudeRepairJournalStore.TryReadEntries(_dir).Count);
    }

    [Fact]
    public void Journal_is_bounded_and_keeps_the_most_recent_entries()
    {
        for (var i = 0; i < 25; i++)
        {
            ClaudeRepairJournalStore.Append(
                _dir,
                new ClaudeRepairJournalEntry(
                    DateTimeOffset.UnixEpoch + TimeSpan.FromDays(i), true, null, null, $"entry {i}"));
        }

        var entries = ClaudeRepairJournalStore.TryReadEntries(_dir);

        Assert.Equal(20, entries.Count);
        Assert.Equal("entry 24", entries[^1].Detail);
        Assert.Equal("entry 5", entries[0].Detail);
    }

    [Fact]
    public void A_corrupt_journal_file_reads_as_empty_instead_of_throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ClaudeRepairJournalStore.FileName), "{ not valid json");

        Assert.Empty(ClaudeRepairJournalStore.TryReadEntries(_dir));
    }
}
