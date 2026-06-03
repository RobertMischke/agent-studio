using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Boot-time, idempotent + crash-safe sweep that converts a project from the
/// lane-folder layout (<c>&lt;projectRoot&gt;/&lt;lane&gt;/&lt;slug&gt;/</c>)
/// to the flat, sharded layout
/// (<c>&lt;projectRoot&gt;/jobs/&lt;bucket&gt;/&lt;taskKey&gt;/</c>): it
/// transfers the lane from folder-position to <c>job.json.state</c>,
/// backfills a stable key for any keyless legacy folder, rebuilds
/// <c>index/*</c>, and removes the emptied lane folders.
///
/// <para>Design (F45 restscope):</para>
/// <list type="bullet">
/// <item><description>
/// The task folder name becomes the stable task key (globally unique per
/// project), which is what removes the duplicate-slug collisions tracked
/// under ASS-571. The external task id (<c>job.json.id</c>, the slug) is
/// left untouched, so <c>/api/tasks</c> responses are unchanged.
/// </description></item>
/// <item><description>
/// Crash-safe: each task is stamped (state + key) in place, then moved with
/// a single atomic <see cref="Directory.Move(string, string)"/>. A crash
/// leaves the task either fully in its lane (retried next boot) or fully
/// under <c>jobs/</c> (skipped next boot), never half-moved.
/// </description></item>
/// <item><description>
/// Idempotent: a second run finds no lane task folders, moves nothing, and
/// just rebuilds the derived index.
/// </description></item>
/// </list>
///
/// <para>
/// This type is pure apart from filesystem effects and the injected
/// <c>mintKey</c> delegate, so it is unit-tested directly against a temp
/// directory. Production wiring (deferred to the supervised cutover) supplies
/// <c>mintKey</c> from <see cref="TaskCounterService"/> + the project
/// shortCode and calls <see cref="Migrate"/> from
/// <c>TaskStateMachine.EnsureStateFoldersAndMigrate</c> behind the
/// EnableNewLayout flag. It is on the TaskFolderAccessIsolation whitelist
/// because the migration legitimately moves and deletes lane folders.
/// </para>
/// </summary>
internal static class TaskLayoutMigrator
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Runs the sweep for a single project root.
    /// </summary>
    /// <param name="projectRoot">The per-project folder holding the lane
    /// folders today and <c>jobs/</c> + <c>index/</c> after migration.</param>
    /// <param name="mintKey">Returns the next full task key (e.g.
    /// <c>"ASS-618"</c>) for a keyless legacy folder. Must be monotonic so
    /// two backfilled folders never collide.</param>
    /// <param name="logger">Structured-log sink.</param>
    public static MigrationResult Migrate(string projectRoot, Func<string> mintKey, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return MigrationResult.Empty;

        var moved = 0;
        var backfilled = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var state in TaskStates.All)
        {
            var laneDir = Path.Combine(projectRoot, state);
            if (!Directory.Exists(laneDir)) continue;

            foreach (var taskDir in Directory.EnumerateDirectories(laneDir).ToList())
            {
                var folderName = Path.GetFileName(taskDir);
                if (!File.Exists(Path.Combine(taskDir, "job.json")))
                {
                    // Not a task folder (no job.json): leave it untouched so a
                    // genuine orphan stays visible for operator review rather
                    // than being silently relocated.
                    skipped++;
                    continue;
                }

                try
                {
                    var (key, wasBackfilled) = StampJob(taskDir, state, folderName, mintKey);
                    if (wasBackfilled) backfilled++;

                    TaskStorageLayout.TryParseKeyNumber(key, out var keyNum);
                    var dest = TaskStorageLayout.JobDir(projectRoot, keyNum, key);

                    if (Directory.Exists(dest))
                    {
                        // Defensive: an atomic Move makes true dual-existence
                        // impossible, so a pre-existing dest means a key
                        // collision. Leave the lane copy in place and flag it
                        // rather than clobbering the migrated folder.
                        errors.Add($"dest already exists for key '{key}' at '{dest}'; lane copy left in place");
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(TaskStorageLayout.BucketDir(projectRoot, keyNum));
                    Directory.Move(taskDir, dest);
                    moved++;
                    logger.LogInformation(
                        "task-migration moved key={Key} fromState={State} to={Location}",
                        key, state, TaskStorageLayout.Location(keyNum, key));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "task-migration failed for {Dir}", taskDir);
                    errors.Add($"{folderName}: {ex.Message}");
                }
            }
        }

        // Rebuild the derived index from the now-migrated jobs/* tree.
        TaskLayoutIndex.Rebuild(projectRoot, logger);

        // Remove emptied lane folders (only when truly empty, so a folder
        // that still holds a skipped orphan is never deleted).
        var lanesRemoved = 0;
        foreach (var state in TaskStates.All)
        {
            var laneDir = Path.Combine(projectRoot, state);
            if (!Directory.Exists(laneDir)) continue;
            if (Directory.EnumerateFileSystemEntries(laneDir).Any()) continue;
            try
            {
                Directory.Delete(laneDir, recursive: false);
                lanesRemoved++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "task-migration: could not remove empty lane {Lane}", laneDir);
            }
        }

        var result = new MigrationResult(moved, backfilled, skipped, lanesRemoved, errors);
        logger.LogInformation(
            "task-migration complete project={Project} moved={Moved} backfilled={Backfilled} skipped={Skipped} lanesRemoved={Lanes} errors={Errors}",
            projectRoot, moved, backfilled, skipped, lanesRemoved, errors.Count);
        return result;
    }

    /// <summary>
    /// Reads <c>job.json</c>, sets <c>state</c> (the authority transfer from
    /// folder position), backfills <c>key</c> when absent, and ensures
    /// <c>id</c> (the slug) is explicit so the external id survives the
    /// folder rename to the key. Preserves the original field order. Writes
    /// the source folder's <c>job.json</c> before the caller moves it, which
    /// is what makes the move crash-safe.
    /// </summary>
    private static (string key, bool backfilled) StampJob(
        string taskDir, string state, string folderName, Func<string> mintKey)
    {
        var jobJsonPath = Path.Combine(taskDir, "job.json");
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                      File.ReadAllText(jobJsonPath), ReadOpts)
                  ?? new Dictionary<string, JsonElement>();

        var existingKey = doc.TryGetValue("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String
            ? keyEl.GetString()
            : null;
        var existingId = doc.TryGetValue("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        var backfilled = false;
        var key = existingKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = mintKey();
            backfilled = true;
        }

        var resolvedId = string.IsNullOrWhiteSpace(existingId) ? folderName : existingId!;

        var updated = new Dictionary<string, object>();
        foreach (var kv in doc)
        {
            updated[kv.Key] = kv.Key switch
            {
                "state" => state,
                "key" => key!,
                "id" => resolvedId,
                _ => kv.Value
            };
        }
        if (!updated.ContainsKey("id")) updated["id"] = resolvedId;
        if (!updated.ContainsKey("state")) updated["state"] = state;
        if (!updated.ContainsKey("key")) updated["key"] = key!;

        File.WriteAllText(jobJsonPath, JsonSerializer.Serialize(updated, WriteOpts));
        return (key!, backfilled);
    }

    public readonly record struct MigrationResult(
        int Moved, int KeysBackfilled, int Skipped, int LanesRemoved, IReadOnlyList<string> Errors)
    {
        public static MigrationResult Empty { get; } =
            new(0, 0, 0, 0, Array.Empty<string>());
    }
}
