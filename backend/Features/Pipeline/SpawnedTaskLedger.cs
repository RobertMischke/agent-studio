using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

/// <summary>One recorded spawn in a source task's <c>.metadata/spawned-tasks.jsonl</c>.</summary>
public sealed record SpawnedTaskRecord
{
    public DateTime At { get; init; }
    /// <summary>Stable display key of the source task (e.g. <c>AGT-2028</c>).</summary>
    public string? SourceKey { get; init; }
    /// <summary>Target project the card was spawned into (watch path or PROJ id, as configured).</summary>
    public string TargetProject { get; init; } = "";
    /// <summary>Display key minted for the spawned card (e.g. <c>WEB-123</c>), when resolvable.</summary>
    public string? TargetKey { get; init; }
    /// <summary>Slug id of the spawned card.</summary>
    public string? TargetJobId { get; init; }
    /// <summary>Short reason the change was judged relevant.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Append-only dedup ledger for the <c>post-task-spawner</c> step (AGT-2028),
/// stored per source task at <c>&lt;jobFolder&gt;/.metadata/spawned-tasks.jsonl</c>
/// (an app-owned sidecar, alongside <c>pipeline-execution.json</c> and
/// <c>prompts.jsonl</c>). It is what makes the spawn idempotent across the
/// reissue loop: a source task re-processed after a reissue reads its own ledger
/// and refuses to spawn again once the per-source budget is spent.
///
/// <para>
/// One JSON object per line so a torn write costs at most one row - the same
/// contract as <see cref="AgentStudio.Tasks.TimelineLog"/>. Reads tolerate blank
/// / malformed lines. Best-effort: an IO failure logs and degrades (an unreadable
/// ledger reads as empty, an unwritable one is swallowed) rather than crashing
/// the post-step.
/// </para>
/// </summary>
public static class SpawnedTaskLedger
{
    public const string RelativePath = ".metadata/spawned-tasks.jsonl";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string PathFor(string jobFolderPath)
        => Path.Combine(jobFolderPath, ".metadata", "spawned-tasks.jsonl");

    /// <summary>Read every recorded spawn for a source task. Missing file =&gt; empty.</summary>
    public static IReadOnlyList<SpawnedTaskRecord> Read(string jobFolderPath, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return Array.Empty<SpawnedTaskRecord>();
        var path = PathFor(jobFolderPath);
        if (!File.Exists(path)) return Array.Empty<SpawnedTaskRecord>();
        var result = new List<SpawnedTaskRecord>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<SpawnedTaskRecord>(line, ReadOpts);
                    if (rec != null) result.Add(rec);
                }
                catch (Exception ex)
                {
                    // Best-effort: skip torn / malformed lines.
                    logger?.LogDebug(ex, "SpawnedTaskLedger: skipping malformed line in {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SpawnedTaskLedger: failed to read {Path}; treating as empty", path);
            return Array.Empty<SpawnedTaskRecord>();
        }
        return result;
    }

    /// <summary>
    /// Whether a fresh spawn is still allowed for this source task under the
    /// per-source budget. A prior spawn into the SAME target project always
    /// blocks (never spawn the same follow-up twice), and once the total count
    /// reaches <paramref name="maxPerSourceTask"/> no further spawn is allowed.
    /// </summary>
    public static bool CanSpawn(
        string jobFolderPath, string targetProject, int maxPerSourceTask, ILogger? logger = null)
    {
        var existing = Read(jobFolderPath, logger);
        if (existing.Count == 0) return true;
        var budget = maxPerSourceTask < 1 ? 1 : maxPerSourceTask;
        if (existing.Count >= budget) return false;
        return !existing.Any(r =>
            string.Equals(r.TargetProject, targetProject, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Append one spawn record. Best-effort; returns false on IO failure.</summary>
    public static bool Append(string jobFolderPath, SpawnedTaskRecord record, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath) || record == null) return false;
        try
        {
            Directory.CreateDirectory(Path.Combine(jobFolderPath, ".metadata"));
            var path = PathFor(jobFolderPath);
            var line = JsonSerializer.Serialize(record, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SpawnedTaskLedger: failed to append record for {Folder}", jobFolderPath);
            return false;
        }
    }
}
