using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the F45 restscope folder-restructure contract for the storage
/// foundation: bucket math, the derived index round-trip, and the boot
/// migration (lane folder -&gt; <c>jobs/&lt;bucket&gt;/&lt;key&gt;</c> with
/// state transferred to <c>job.json</c>). The migration is the piece that
/// touches live task data at boot, so every acceptance criterion is pinned
/// here against a temp directory before the supervised cutover wires it in.
/// </summary>
public class TaskLayoutMigrationTests : IDisposable
{
    private readonly string _root;
    private int _mintCounter = 100;

    public TaskLayoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-layout-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string MintKey() => $"TST-{_mintCounter++}";

    // ---- bucket math -------------------------------------------------------

    [Theory]
    [InlineData(0, "000")]
    [InlineData(1, "000")]
    [InlineData(617, "000")]
    [InlineData(999, "000")]
    [InlineData(1000, "001")]
    [InlineData(1500, "001")]
    [InlineData(12345, "012")]
    public void Bucket_FloorsByThousand_ZeroPaddedToThree(int keyNumber, string expected)
    {
        Assert.Equal(expected, TaskStorageLayout.Bucket(keyNumber));
    }

    [Theory]
    [InlineData("TST-617", true, 617)]
    [InlineData("ASS-1500", true, 1500)]
    [InlineData("LD-1", true, 1)]
    [InlineData("TST-0", false, 0)]
    [InlineData("TST-abc", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParseKeyNumber_ParsesTrailingPositiveInteger(string? key, bool ok, int expected)
    {
        Assert.Equal(ok, TaskStorageLayout.TryParseKeyNumber(key, out var n));
        Assert.Equal(expected, n);
    }

    // ---- migration ---------------------------------------------------------

    [Fact]
    public void Migrate_MovesLaneTaskIntoBucket_AndStampsStateFromLane()
    {
        SeedLaneTask(TaskStates.Ready, "do-the-thing", key: "TST-617");

        var result = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Errors);

        var jobJson = Path.Combine(_root, "jobs", "000", "TST-617", "job.json");
        Assert.True(File.Exists(jobJson), "task should live under jobs/000/TST-617");
        Assert.Equal(TaskStates.Ready, Field(jobJson, "state"));

        // Old lane folder is gone.
        Assert.False(Directory.Exists(Path.Combine(_root, TaskStates.Ready, "do-the-thing")));
    }

    [Fact]
    public void Migrate_LeavesExternalIdUnchanged_WhenFolderRenamedToKey()
    {
        // The folder name becomes the key, but job.json.id (the slug, which
        // the external /api/tasks id is derived from) must not change.
        SeedLaneTask(TaskStates.Progress, "keep-this-id", key: "TST-42");

        TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        var jobJson = Path.Combine(_root, "jobs", "000", "TST-42", "job.json");
        Assert.Equal("keep-this-id", Field(jobJson, "id"));
    }

    [Fact]
    public void Migrate_BackfillsKey_ForKeylessLegacyFolder()
    {
        SeedLaneTask(TaskStates.Backlog, "legacy-no-key", key: null);

        var result = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.KeysBackfilled);

        var jobJson = Path.Combine(_root, "jobs", "000", "TST-100", "job.json");
        Assert.True(File.Exists(jobJson));
        Assert.Equal("TST-100", Field(jobJson, "key"));
        Assert.Equal("legacy-no-key", Field(jobJson, "id"));
    }

    [Fact]
    public void Migrate_DuplicateSlugAcrossLanes_LandsInDistinctFolders_NoCollision()
    {
        // The exact ASS-571 scenario: same slug parked in two lanes. Under
        // the new layout each lands under its own key, so no 409 / collision.
        SeedLaneTask(TaskStates.Ready, "dup", key: "TST-1");
        SeedLaneTask(TaskStates.Archive, "dup", key: "TST-2");

        var result = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.Equal(2, result.Moved);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(_root, "jobs", "000", "TST-1", "job.json")));
        Assert.True(File.Exists(Path.Combine(_root, "jobs", "000", "TST-2", "job.json")));

        // Both keep the same external id.
        Assert.Equal("dup", Field(Path.Combine(_root, "jobs", "000", "TST-1", "job.json"), "id"));
        Assert.Equal("dup", Field(Path.Combine(_root, "jobs", "000", "TST-2", "job.json"), "id"));

        var byKey = TaskLayoutIndex.ReadByKey(_root);
        Assert.Equal("000/TST-1", byKey["TST-1"]);
        Assert.Equal("000/TST-2", byKey["TST-2"]);
    }

    [Fact]
    public void Migrate_HigherKeyNumber_ShardsIntoBucket001()
    {
        SeedLaneTask(TaskStates.Ready, "high-number", key: "TST-1500");

        TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.True(File.Exists(Path.Combine(_root, "jobs", "001", "TST-1500", "job.json")));
        Assert.Equal("001/TST-1500", TaskLayoutIndex.ReadByKey(_root)["TST-1500"]);
    }

    [Fact]
    public void Migrate_BuildsIndex_ByStateAndByKey()
    {
        SeedLaneTask(TaskStates.Ready, "a", key: "TST-10");
        SeedLaneTask(TaskStates.Ready, "b", key: "TST-11");
        SeedLaneTask(TaskStates.Progress, "c", key: "TST-12");

        TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        var byState = TaskLayoutIndex.ReadByState(_root);
        Assert.Equal(new[] { "000/TST-10", "000/TST-11" }, byState[TaskStates.Ready]);
        Assert.Equal(new[] { "000/TST-12" }, byState[TaskStates.Progress]);

        var byKey = TaskLayoutIndex.ReadByKey(_root);
        Assert.Equal("000/TST-10", byKey["TST-10"]);
        Assert.Equal("000/TST-12", byKey["TST-12"]);
    }

    [Fact]
    public void Migrate_RemovesEmptyLaneFolders()
    {
        SeedLaneTask(TaskStates.Ready, "only-task", key: "TST-5");

        TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.False(Directory.Exists(Path.Combine(_root, TaskStates.Ready)));
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunMovesNothing_IndexStable()
    {
        SeedLaneTask(TaskStates.Ready, "x", key: "TST-7");
        SeedLaneTask(TaskStates.Progress, "y", key: "TST-8");

        var first = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);
        var firstByState = JsonSerializer.Serialize(TaskLayoutIndex.ReadByState(_root));

        var second = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);
        var secondByState = JsonSerializer.Serialize(TaskLayoutIndex.ReadByState(_root));

        Assert.Equal(2, first.Moved);
        Assert.Equal(0, second.Moved);
        Assert.Equal(0, second.KeysBackfilled);
        Assert.Empty(second.Errors);
        Assert.Equal(firstByState, secondByState);
    }

    [Fact]
    public void Migrate_CrashResume_CompletesRemaining_WithoutTouchingAlreadyMigrated()
    {
        // Simulate a crash mid-sweep: one task already relocated under jobs/
        // (stamped), one still sitting in its lane (also already stamped, as
        // it would be if the crash hit between stamp and move).
        SeedMigratedTask("000", "TST-1", state: TaskStates.Ready, id: "already-done");
        SeedLaneTask(TaskStates.Progress, "still-in-lane", key: "TST-2", state: TaskStates.Progress);

        var result = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Errors);

        // Already-migrated task untouched.
        Assert.True(File.Exists(Path.Combine(_root, "jobs", "000", "TST-1", "job.json")));
        Assert.Equal("already-done", Field(Path.Combine(_root, "jobs", "000", "TST-1", "job.json"), "id"));

        // Lane task now relocated.
        Assert.True(File.Exists(Path.Combine(_root, "jobs", "000", "TST-2", "job.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, TaskStates.Progress, "still-in-lane")));

        var byKey = TaskLayoutIndex.ReadByKey(_root);
        Assert.Equal("000/TST-1", byKey["TST-1"]);
        Assert.Equal("000/TST-2", byKey["TST-2"]);
    }

    [Fact]
    public void Migrate_NonTaskFolder_WithoutJobJson_IsLeftInPlace()
    {
        // A genuine orphan (no job.json) must not be silently relocated.
        var orphan = Path.Combine(_root, TaskStates.Ready, "orphan-no-json");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "prompt.md"), "leftover");

        var result = TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.Equal(0, result.Moved);
        Assert.True(Directory.Exists(orphan), "orphan folder should be left for operator review");
    }

    [Fact]
    public void Index_AtomicWrite_LeavesNoTempFile()
    {
        SeedLaneTask(TaskStates.Ready, "t", key: "TST-9");
        TaskLayoutMigrator.Migrate(_root, MintKey, NullLogger.Instance);

        Assert.False(File.Exists(Path.Combine(_root, "index", "by-state.json.tmp")));
        Assert.False(File.Exists(Path.Combine(_root, "index", "by-key.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_root, "index", "by-state.json")));
        Assert.True(File.Exists(Path.Combine(_root, "index", "by-key.json")));
    }

    // ---- helpers -----------------------------------------------------------

    private void SeedLaneTask(string lane, string slug, string? key, string? state = null)
    {
        var dir = Path.Combine(_root, lane, slug);
        Directory.CreateDirectory(dir);
        var doc = new Dictionary<string, object?>
        {
            ["id"] = slug,
            ["title"] = slug + " title",
        };
        // Pre-migration job.json may carry a stale or absent state; the
        // migration overwrites it from the folder position. Only set it when
        // the test wants to assert the stamp overrides a stale value.
        if (state != null) doc["state"] = state;
        if (key != null) doc["key"] = key;
        File.WriteAllText(Path.Combine(dir, "job.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SeedMigratedTask(string bucket, string key, string state, string id)
    {
        var dir = Path.Combine(_root, "jobs", bucket, key);
        Directory.CreateDirectory(dir);
        var doc = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = id + " title",
            ["state"] = state,
            ["key"] = key,
        };
        File.WriteAllText(Path.Combine(dir, "job.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Field(string jobJsonPath, string field)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(jobJsonPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return doc.TryGetValue(field, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
