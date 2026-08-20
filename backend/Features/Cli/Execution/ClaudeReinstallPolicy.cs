using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Cli;

/// <summary>
/// What to do about a missing <c>claude</c> npm shim once
/// <see cref="NpmShimHealer"/>'s orphan/stub repairs (steps 1-4) have already
/// run and the shim is still gone. The caller only ever consults this policy
/// from inside its own <c>shim is missing</c> check, so "the shim isn't
/// actually missing" is not a case this policy needs to represent - keeping
/// it out avoids a decision branch nothing can ever select. Kept as a plain
/// decision separate from the npm spawn and file I/O per the repo's
/// pure-policy convention (docs/quality/dotnet-backend.md) so the branching
/// can be pinned with a direct matrix test instead of a mock-heavy process
/// test.
/// </summary>
public enum ClaudeReinstallDecision
{
    /// <summary>
    /// No <c>@anthropic-ai/claude-code</c> package on disk to reinstall from.
    /// This is a different, riskier situation than a broken existing install
    /// (auto-provisioning a fresh global install was never observed as the
    /// fix in either incident) so the policy deliberately does not attempt it.
    /// </summary>
    TrulyUninstalled,

    /// <summary>A full reinstall already ran inside the cooldown window.</summary>
    CooldownActive,

    /// <summary>Package present, cooldown elapsed: go.</summary>
    Attempt,
}

public static class ClaudeReinstallPolicy
{
    /// <summary>
    /// Bounds how often <c>npm install -g</c> may run. The observed breakage
    /// (docs/operations/live-improvement-log/index.html, second sighting)
    /// recurs on the order of days, not minutes; an hour keeps a still-broken
    /// installer from being retried on every card pickup while still healing
    /// well within an operator's shift.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    public static ClaudeReinstallDecision Decide(
        bool packagePresent,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset now,
        TimeSpan? cooldown = null)
    {
        if (!packagePresent) return ClaudeReinstallDecision.TrulyUninstalled;
        if (lastAttemptAt is { } last && now - last < (cooldown ?? Cooldown))
            return ClaudeReinstallDecision.CooldownActive;
        return ClaudeReinstallDecision.Attempt;
    }
}

/// <summary>One recorded full-reinstall attempt, kept for root-cause forensics.</summary>
public sealed record ClaudeRepairJournalEntry(
    [property: JsonPropertyName("attemptedAt")] DateTimeOffset AttemptedAt,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("versionBefore")] string? VersionBefore,
    [property: JsonPropertyName("versionAfter")] string? VersionAfter,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>
/// Durable record of full-reinstall attempts, colocated with the npm global
/// bin it repairs so no new host-wide state directory is required. Read
/// before every attempt (cooldown gate) and appended after every attempt
/// (root-cause trail: repeated version jumps are the auto-update evidence
/// an operator otherwise has to reconstruct from memory).
/// </summary>
public static class ClaudeRepairJournalStore
{
    public const string FileName = ".claude-cli-repair-journal.json";

    /// <summary>Keep the trail bounded; only recent history matters for root-cause.</summary>
    private const int MaxRetainedEntries = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static DateTimeOffset? TryReadLastAttemptAt(string npmBin, ILogger? logger = null)
        => TryReadEntries(npmBin, logger).LastOrDefault()?.AttemptedAt;

    public static IReadOnlyList<ClaudeRepairJournalEntry> TryReadEntries(string npmBin, ILogger? logger = null)
    {
        var path = Path.Combine(npmBin, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ClaudeRepairJournalEntry>>(File.ReadAllText(path), Options)
                   ?? [];
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read claude repair journal at {Path}", path);
            return [];
        }
    }

    public static void Append(string npmBin, ClaudeRepairJournalEntry entry, ILogger? logger = null)
    {
        try
        {
            var path = Path.Combine(npmBin, FileName);
            var entries = TryReadEntries(npmBin, logger).ToList();
            entries.Add(entry);
            if (entries.Count > MaxRetainedEntries)
                entries = entries.Skip(entries.Count - MaxRetainedEntries).ToList();

            // File.Move(overwrite: true) is not reader-transparent on Windows
            // (this store only ever runs there): a concurrent TryReadEntries
            // call can hit a sharing violation or a truncated read mid-swap.
            // AtomicJsonFileWriter's File.Replace-with-backup-and-retry is the
            // repo's existing answer to exactly that race.
            new AtomicJsonFileWriter().Write(path, JsonSerializer.Serialize(entries, Options));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist claude repair journal entry in {NpmBin}", npmBin);
        }
    }
}
