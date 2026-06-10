using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Append-only record describing one decision the
/// <c>ReviewDecisionOrchestrator</c> made about a 4-review job that ended
/// in <c>[[TASK_NEEDS_INPUT]]</c>. Persisted to
/// <c>{workspace}/logs/decisions/{project}.jsonl</c>; consumed by the
/// Layer 3 review and the planned executive-summary surface.
///
/// Schema lives at <c>docs/schemas/orchestrator-decision.schema.json</c>;
/// keep the two in sync.
/// </summary>
public sealed record ReviewDecisionRecord(
    DateTime CreatedAt,
    string JobId,
    string Project,
    ReviewDecisionKind Kind,
    string Reason,
    string Prompt,
    string Response,
    string FollowUp);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewDecisionKind
{
    Reissue,
    Escalate,
    AcceptAsDone,
    Skipped
}

/// <summary>
/// Writes <see cref="ReviewDecisionRecord"/>s into the per-project decision
/// journal. Static + path-based so tests do not need to instantiate a service.
/// </summary>
public static class ReviewDecisionLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // The auto-review read-only pool reviews several tasks concurrently, so
    // Append / ReadAll can be hit from multiple threads at once. The journal is
    // a single append-only file per project; serialise access through one
    // process-wide lock so a concurrent append cannot interleave a line or have
    // its OS file handle collide with a concurrent read.
    private static readonly object FileLock = new();

    public static string DecisionsDir(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, "logs", "decisions");
    }

    public static string DecisionsFile(string workspaceRoot, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return Path.Combine(DecisionsDir(workspaceRoot), $"{project}.jsonl");
    }

    public static void Append(string workspaceRoot, ReviewDecisionRecord record)
    {
        var dir = DecisionsDir(workspaceRoot);
        Directory.CreateDirectory(dir);
        var path = DecisionsFile(workspaceRoot, record.Project);
        var line = JsonSerializer.Serialize(record, Json);
        lock (FileLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static IReadOnlyList<ReviewDecisionRecord> ReadAll(string workspaceRoot, string project)
    {
        var path = DecisionsFile(workspaceRoot, project);
        if (!File.Exists(path)) return Array.Empty<ReviewDecisionRecord>();
        var result = new List<ReviewDecisionRecord>();
        lock (FileLock)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<ReviewDecisionRecord>(line, Json);
                    if (rec != null) result.Add(rec);
                }
                catch (JsonException __ex) { SilentCatch.Note(__ex, "ReviewDecisionRecord: skip malformed lines; the file is append-only"); /* skip malformed lines; the file is append-only */ }
            }
        }
        return result;
    }
}
