using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Reader / appender for <c>results/review-evidence.jsonl</c>: the
/// task-level review-evidence file documented in
/// <c>docs/contracts/filesystem.md</c>.
///
/// The file is JSON-Lines, append-only, and per-job. Each line is one
/// <see cref="ReviewEvidenceEntry"/>. The parser is defensively permissive:
/// blank lines, malformed JSON, or unknown enum values never throw. A
/// finding without an <c>id</c> or <c>title</c> is skipped with a warning so
/// a single bad row cannot wedge the panel for the rest of the file.
///
/// Mutating an existing finding (acknowledge, attach a follow-up) is done by
/// appending a new row with the same id and the updated fields. Readers
/// fold the file into latest-per-id ordered by file position.
/// </summary>
internal static class ReviewEvidenceLog
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Reads the evidence file and returns the latest entry per id, in the
    /// order each id first appeared. Returns an empty list when the file is
    /// missing.
    /// </summary>
    public static List<ReviewEvidenceEntry> ReadLatestPerId(string jobFolder, ILogger? logger = null)
    {
        var path = TaskPaths.ReviewEvidenceLog(jobFolder);
        if (!File.Exists(path)) return [];

        var firstSeenOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var latest = new Dictionary<string, ReviewEvidenceEntry>(StringComparer.Ordinal);
        var counter = 0;

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read review evidence file at {Path}", path);
            return [];
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            ReviewEvidenceEntry? entry;
            try
            {
                entry = ParseLine(raw);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Skipping malformed review-evidence line in {Path}", path);
                continue;
            }
            if (entry == null) continue;

            if (!firstSeenOrder.ContainsKey(entry.Id))
            {
                firstSeenOrder[entry.Id] = counter++;
            }
            latest[entry.Id] = entry;
        }

        return latest.Values
            .OrderBy(e => firstSeenOrder[e.Id])
            .ToList();
    }

    /// <summary>
    /// Parses a single JSONL line into a <see cref="ReviewEvidenceEntry"/>.
    /// Returns null when the line is missing required fields. Throws only on
    /// catastrophic JSON errors that the caller treats as "skip this line".
    /// Public for unit tests.
    /// </summary>
    public static ReviewEvidenceEntry? ParseLine(string line)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(line, TaskJsonFile.ReadOpts);
        if (doc.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(doc, "id");
        var title = ReadString(doc, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;

        return new ReviewEvidenceEntry
        {
            Id = id!,
            Source = ReviewEvidenceSources.Normalize(ReadString(doc, "source")),
            Severity = ReviewEvidenceSeverities.Normalize(ReadString(doc, "severity")),
            Title = title!,
            Body = ReadString(doc, "body"),
            CreatedAt = ReadDateTime(doc, "createdAt") ?? DateTime.UtcNow,
            RunIndex = ReadInt(doc, "runIndex"),
            Artifacts = ReadStringArray(doc, "artifacts"),
            FileRefs = ReadStringArray(doc, "fileRefs"),
            Acknowledged = ReadBool(doc, "acknowledged") ?? false,
            FollowupJobId = ReadString(doc, "followupJobId")
        };
    }

    /// <summary>
    /// Appends one entry to the evidence file, creating the parent
    /// <c>results/</c> directory if needed. Used by mutating endpoints
    /// (acknowledge, attach follow-up id). Producers writing the file from
    /// outside the API still write directly with their own tooling — this
    /// is just a convenience.
    /// </summary>
    public static void Append(string jobFolder, ReviewEvidenceEntry entry)
    {
        var dir = TaskPaths.ResultsDir(jobFolder);
        Directory.CreateDirectory(dir);
        var path = TaskPaths.ReviewEvidenceLog(jobFolder);
        var json = JsonSerializer.Serialize(entry, WriteOpts);
        File.AppendAllText(path, json + "\n", Encoding.UTF8);
    }

    private static string? ReadString(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    private static int? ReadInt(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : null;
    }

    private static bool? ReadBool(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTime? ReadDateTime(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
    }

    private static List<string> ReadStringArray(JsonElement doc, string name)
    {
        if (!doc.TryGetProperty(name, out var v)) return [];
        if (v.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }
}
