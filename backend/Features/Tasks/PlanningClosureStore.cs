using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2069 — the persisted "bewusst keine Umsetzung" (deliberately no
/// follow-up) declaration for a planning task, stored per source task at
/// <c>&lt;jobFolder&gt;/.metadata/planning-closure.json</c> (an app-owned
/// sidecar, alongside <c>spawned-tasks.jsonl</c> and
/// <c>pipeline-execution.json</c>). It lets a planning task satisfy the
/// spawn-contract completion gate without producing follow-up cards, by an
/// explicit operator call rather than a silent slip.
///
/// <para>Kept as a tiny static store (no DI) mirroring
/// <see cref="AgentStudio.Pipeline.SpawnedTaskLedger"/>: a single small JSON
/// object, best-effort IO (an unreadable file reads as "not declared", an
/// unwritable one is swallowed and logged) so a read overlay never crashes on
/// it. Writing to the app-owned <c>.metadata</c> folder keeps this off
/// <c>task.json</c>, so no scanner migration is needed.</para>
/// </summary>
public static class PlanningClosureStore
{
    public const string RelativePath = ".metadata/planning-closure.json";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string PathFor(string jobFolderPath)
        => Path.Combine(jobFolderPath, ".metadata", "planning-closure.json");

    /// <summary>The on-disk record. Absent file =&gt; no declaration.</summary>
    public sealed record PlanningClosureRecord
    {
        public bool NoFollowUpDeclared { get; init; }
        public string? Reason { get; init; }
        public DateTime? DeclaredAt { get; init; }
        public string? DeclaredBy { get; init; }
    }

    /// <summary>Read the declaration for a planning task. Missing / unreadable file =&gt; null.</summary>
    public static PlanningClosureRecord? Read(string jobFolderPath, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return null;
        var path = PathFor(jobFolderPath);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<PlanningClosureRecord>(json, ReadOpts);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlanningClosureStore: failed to read {Path}; treating as no declaration", path);
            return null;
        }
    }

    /// <summary>
    /// Persist (or clear) the no-follow-up declaration. <paramref name="declared"/>
    /// false removes the file so the gate reverts to "not satisfied". Best-effort;
    /// returns false on IO failure.
    /// </summary>
    public static bool Write(
        string jobFolderPath, bool declared, string? reason, string? declaredBy, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return false;
        var path = PathFor(jobFolderPath);
        try
        {
            if (!declared)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            Directory.CreateDirectory(Path.Combine(jobFolderPath, ".metadata"));
            var record = new PlanningClosureRecord
            {
                NoFollowUpDeclared = true,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                DeclaredAt = DateTime.UtcNow,
                DeclaredBy = string.IsNullOrWhiteSpace(declaredBy) ? null : declaredBy.Trim(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(record, WriteOpts));
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlanningClosureStore: failed to write {Path}", path);
            return false;
        }
    }
}
