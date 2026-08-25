using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Projects;

/// <summary>
/// One reconstructed completion timestamp for a terminal task whose ledger
/// records no entry into <c>6-completed</c> (it predates the
/// <c>lane_changed</c> kind, or was archived without one). Reconstructed
/// offline from durable evidence by
/// <c>scripts/backfill-cycle-time-completions.mjs</c>; the reader treats it
/// as an approximate completion anchor for the lead-time rollup only and
/// never invents stage durations from it.
/// </summary>
public sealed record CycleTimeBackfillEntry(
    DateTime CompletedAt,
    /// <summary>
    /// Evidence the timestamp came from, best first:
    /// <c>git-completed-move</c> (workspace-repo commit that moved the task
    /// folder into a completed lane), <c>git-archive-move</c> (commit that
    /// moved it into an archive lane), <c>task-entered-lane</c>
    /// (<c>task.json</c> <c>enteredLaneAt</c> while terminal),
    /// <c>git-terminal-first-seen</c> (first commit that contains the task
    /// already terminal; an upper bound), <c>status-mtime</c> (last resort).
    /// </summary>
    string Source,
    /// <summary><c>high</c>, <c>medium</c>, or <c>low</c>.</summary>
    string Confidence,
    /// <summary>Workspace-repo commit the timestamp was taken from, for git sources.</summary>
    string? Commit);

/// <summary>
/// Reads the per-project completion-backfill sidecar
/// (<c>&lt;watchPath&gt;/.metadata/cycle-time-backfill.json</c>). The file is
/// generated and committed in the workspace repo, so it is auditable and
/// re-runnable; this reader is deliberately tolerant: a missing file, a
/// malformed file, or a malformed entry degrades to "no backfill", never to
/// an error.
/// </summary>
public static class CycleTimeBackfillSidecar
{
    public const string FileName = "cycle-time-backfill.json";
    public const string MetadataDirName = ".metadata";

    public static string PathFor(string watchPath) =>
        Path.Combine(watchPath, MetadataDirName, FileName);

    private static readonly IReadOnlyDictionary<string, CycleTimeBackfillEntry> Empty =
        new Dictionary<string, CycleTimeBackfillEntry>();

    /// <summary>Entries keyed by task key (case-insensitive). Empty when the file is missing or unreadable.</summary>
    public static IReadOnlyDictionary<string, CycleTimeBackfillEntry> Load(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return Empty;
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
        return Parse(json);
    }

    /// <summary>Tolerant parse: an invalid entry is skipped, an invalid document yields no entries.</summary>
    internal static IReadOnlyDictionary<string, CycleTimeBackfillEntry> Parse(string json)
    {
        var entries = new Dictionary<string, CycleTimeBackfillEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("entries", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            foreach (var property in map.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)
                    || property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var completedAt = ReadUtc(property.Value, "completedAt");
                var source = ReadString(property.Value, "source");
                if (completedAt is null || source is null) continue;
                entries[property.Name.Trim()] = new CycleTimeBackfillEntry(
                    completedAt.Value,
                    source,
                    ReadString(property.Value, "confidence") ?? "low",
                    ReadString(property.Value, "commit"));
            }
        }
        catch (JsonException)
        {
            return Empty;
        }
        return entries;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static DateTime? ReadUtc(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        if (raw is null) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
