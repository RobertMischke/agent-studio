using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F33 stable-reference-key contract: once a task is minted a display key
/// (<c>DEM-7</c>) the number is persisted in <c>task.json</c> and is
/// <b>immutable</b> for the life of the task. The number must survive every
/// non-mint board operation - create, delete, reorder, lane-move, re-scan -
/// and the idempotent boot migrations (backfill + dedup) must never disturb
/// an existing, already-unique key.
///
/// <para>Background: keys were observed to drift (e.g. ASS-833 -> ASS-934)
/// after the F33 cleanup. The drift came from the duplicate-key dedup sweep
/// re-keying collision losers, not from any per-read recomputation - the
/// scanner reads <c>key</c> straight off disk (<see cref="TaskScannerService"/>
/// <c>ReadReferenceKey</c>). These tests fence the invariant so a future
/// change that recomputes a key from order/index/count, or that lets a
/// routine mutation touch the <c>key</c> field, fails loudly here.</para>
/// </summary>
public class TaskKeyStabilityTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";
    private const string ShortCode = "DEM"; // ShortCodeGenerator.Derive("Demo")

    public TaskKeyStabilityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "key-stability-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // AC#1 / AC#2 - Add mints the next monotonic number and leaves every
    // pre-existing key untouched.
    // ------------------------------------------------------------------

    [Fact]
    public void Add_MintsNextMonotonicKey_AndLeavesExistingKeysUntouched()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var first = mutations.CreateJob(NewRequest("Alpha"));
        var second = mutations.CreateJob(NewRequest("Beta"));
        var third = mutations.CreateJob(NewRequest("Gamma"));

        Assert.Equal("DEM-1", KeyOf(scanner, first));
        Assert.Equal("DEM-2", KeyOf(scanner, second));
        Assert.Equal("DEM-3", KeyOf(scanner, third));

        var before = KeySnapshot(scanner);

        var fourth = mutations.CreateJob(NewRequest("Delta"));
        Assert.Equal("DEM-4", KeyOf(scanner, fourth));

        // Adding the 4th card must not renumber any of the first three.
        AssertKeysUnchanged(before, scanner, ignoreIds: fourth);
    }

    // ------------------------------------------------------------------
    // AC#2 - a deleted number is a permanent gap; it is never reused, and
    // surviving keys are untouched.
    // ------------------------------------------------------------------

    [Fact]
    public void Delete_PreservesSurvivingKeys_AndNeverReusesTheDeletedNumber()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var a = mutations.CreateJob(NewRequest("Alpha"));
        var b = mutations.CreateJob(NewRequest("Beta"));
        var c = mutations.CreateJob(NewRequest("Gamma"));
        Assert.Equal("DEM-2", KeyOf(scanner, b));

        Assert.True(machine.DeleteJob(b, _watchPath));

        // Surviving cards keep their numbers.
        Assert.Equal("DEM-1", KeyOf(scanner, a));
        Assert.Equal("DEM-3", KeyOf(scanner, c));

        // The next mint advances past the high-water mark; the freed DEM-2 is
        // left as a permanent gap rather than recycled.
        var d = mutations.CreateJob(NewRequest("Delta"));
        Assert.Equal("DEM-4", KeyOf(scanner, d));
        Assert.Equal("DEM-1", KeyOf(scanner, a));
        Assert.Equal("DEM-3", KeyOf(scanner, c));
    }

    // ------------------------------------------------------------------
    // AC#4 - reorder changes the order field only; no key moves.
    // ------------------------------------------------------------------

    [Fact]
    public void Reorder_DoesNotChangeAnyKey()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var a = mutations.CreateJob(NewRequest("Alpha"));
        var b = mutations.CreateJob(NewRequest("Beta"));
        var c = mutations.CreateJob(NewRequest("Gamma"));

        var before = KeySnapshot(scanner);

        // Reverse the on-board order.
        var reordered = new List<TaskOrderItem>
        {
            new() { JobId = c, WatchPath = _watchPath },
            new() { JobId = b, WatchPath = _watchPath },
            new() { JobId = a, WatchPath = _watchPath },
        };
        Assert.True(machine.ReorderJobs(reordered));

        AssertKeysUnchanged(before, scanner);
    }

    // ------------------------------------------------------------------
    // AC#1 / AC#4 - moving a card between lanes preserves its key.
    // ------------------------------------------------------------------

    [Fact]
    public void LaneMove_PreservesKey()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var id = mutations.CreateJob(NewRequest("Alpha"));
        Assert.Equal("DEM-1", KeyOf(scanner, id));

        var outcome = machine.MoveJob(id, TaskStates.Archive, _watchPath);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        scanner.InvalidateCache();
        var moved = scanner.FindJob(id, _watchPath)!;
        Assert.Equal(TaskStates.Archive, moved.State);
        Assert.Equal("DEM-1", moved.Key);
    }

    // ------------------------------------------------------------------
    // AC#1 - a plain re-scan (cache drop) reads the same persisted keys; the
    // number is read off disk, never recomputed from board position.
    // ------------------------------------------------------------------

    [Fact]
    public void ReScan_ReturnsIdenticalKeys()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(NewRequest("Alpha"));
        mutations.CreateJob(NewRequest("Beta"));
        mutations.CreateJob(NewRequest("Gamma"));

        var before = KeySnapshot(scanner);

        scanner.InvalidateCache();
        var after = KeySnapshot(scanner);

        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------------
    // AC#1 / AC#3 - the boot migrations are inert on an already-keyed,
    // collision-free board: backfill stamps nothing and dedup re-keys
    // nothing, so a second migration pass cannot drift the frozen keys.
    // ------------------------------------------------------------------

    [Fact]
    public void Migration_BackfillAndDedup_AreNoOpsWhenKeysAreUniqueAndPresent()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(NewRequest("Alpha"));
        mutations.CreateJob(NewRequest("Beta"));
        mutations.CreateJob(NewRequest("Gamma"));

        var before = KeySnapshot(scanner);

        Assert.Equal(0, mutations.BackfillTaskKeys());
        Assert.Equal(0, mutations.DeduplicateTaskKeys());

        AssertKeysUnchanged(before, scanner);
    }

    // ------------------------------------------------------------------
    // AC#3 - the one-time fixation stamps a key onto a legacy keyless task
    // *above* the on-disk high-water mark, without touching any existing key.
    // ------------------------------------------------------------------

    [Fact]
    public void Backfill_StampsKeylessJobAboveMax_WithoutTouchingExistingKeys()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // Existing keyed cards plus one legacy folder with no key field.
        SeedKeyedJob(TaskStates.Archive, "alpha", "DEM-1", "2026-05-31T10:00:00Z");
        SeedKeyedJob(TaskStates.Archive, "beta", "DEM-3", "2026-05-31T10:01:00Z");
        SeedKeylessJob(TaskStates.Ready, "legacy", "2026-05-31T10:02:00Z");
        scanner.InvalidateCache();

        Assert.Equal(1, mutations.BackfillTaskKeys());

        scanner.InvalidateCache();
        // Pre-existing keys are untouched.
        Assert.Equal("DEM-1", KeyOf(scanner, "alpha"));
        Assert.Equal("DEM-3", KeyOf(scanner, "beta"));
        // The legacy card is stamped above the on-disk maximum (DEM-3 -> DEM-4),
        // never reusing the DEM-2 gap.
        Assert.Equal("DEM-4", KeyOf(scanner, "legacy"));

        // Idempotent: a second pass stamps nothing.
        Assert.Equal(0, mutations.BackfillTaskKeys());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string? KeyOf(TaskScannerService scanner, string jobId) =>
        scanner.FindJob(jobId, _watchPath)?.Key;

    private Dictionary<string, string?> KeySnapshot(TaskScannerService scanner) =>
        scanner.ScanAllJobs()
            .Where(j => string.Equals(j.WatchPath, _watchPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(j => j.Id, j => j.Key, StringComparer.Ordinal);

    private void AssertKeysUnchanged(
        Dictionary<string, string?> before,
        TaskScannerService scanner,
        params string[] ignoreIds)
    {
        scanner.InvalidateCache();
        var after = KeySnapshot(scanner);
        foreach (var (id, key) in before)
        {
            if (ignoreIds.Contains(id)) continue;
            Assert.True(after.TryGetValue(id, out var now),
                $"task {id} vanished after the operation");
            Assert.Equal(key, now);
        }
    }

    private CreateJobRequest NewRequest(string title, string? targetState = TaskStates.Ready) => new()
    {
        Title = title,
        WatchPath = _watchPath,
        Agent = "claude",
        TargetState = targetState
    };

    private void SeedKeyedJob(string state, string slug, string key, string createdAtIso)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"order\":1,\"agent\":\"claude\",\"key\":\"{key}\",\"createdAt\":\"{createdAtIso}\"}}");
    }

    private void SeedKeylessJob(string state, string slug, string createdAtIso)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"order\":1,\"agent\":\"claude\",\"createdAt\":\"{createdAtIso}\"}}");
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations, ProjectRegistry registry) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        // Register the project so MintTaskKey / backfill can resolve a short code.
        registry.EnsureProjectForStorage(_watchPath, "Demo", DefaultWorkspace.Id);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex);
        return (machine, scanner, mutations, registry);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
