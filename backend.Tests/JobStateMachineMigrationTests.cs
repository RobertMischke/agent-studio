using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0025 boot-time migration: <c>TaskStateMachine.EnsureStateFoldersAndMigrate</c>
/// renames the pre-three-stage-review numbered lanes (<c>4-review</c>,
/// <c>5-completed</c>, <c>6-archive</c>) to the new layout, rewrites each
/// job.json's <c>state</c> field, and is idempotent on a second pass.
/// </summary>
public class TaskStateMachineMigrationTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskStateMachineMigrationTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-migr-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Migrate_RenamesLegacyNumberedLanes_AndRewritesJobJsonState()
    {
        SeedLegacyJob("4-review", "alpha");
        SeedLegacyJob("4-review", "beta");
        SeedLegacyJob("5-completed", "gamma");
        SeedLegacyJob("6-archive", "delta");

        var (machine, _) = BuildMachine();
        machine.EnsureStateFoldersAndMigrate();

        // Legacy lanes are gone (or at least empty enough to be cleaned up).
        Assert.False(Directory.Exists(Path.Combine(_watchPath, "4-review")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, "5-completed")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, "6-archive")));

        // The new lanes hold the moved jobs and the canonical state name
        // is written back into each job.json so the in-memory view stays
        // aligned with disk.
        AssertMovedAndStateMatches(TaskStates.AutoReview, "alpha");
        AssertMovedAndStateMatches(TaskStates.AutoReview, "beta");
        AssertMovedAndStateMatches(TaskStates.Completed, "gamma");
        AssertMovedAndStateMatches(TaskStates.Archive,   "delta");

        // The migration counter mirrors the "moved 4 jobs" log line.
        Assert.Equal(4, machine.LastNumberedLaneMigrationCount);
    }

    [Fact]
    public void Migrate_IsIdempotent_OnSecondCall()
    {
        SeedLegacyJob("4-review", "alpha");
        var (machine, _) = BuildMachine();

        machine.EnsureStateFoldersAndMigrate();
        Assert.Equal(1, machine.LastNumberedLaneMigrationCount);

        // Second call sees no legacy lane and does not rewrite anything.
        machine.EnsureStateFoldersAndMigrate();
        Assert.Equal(0, machine.LastNumberedLaneMigrationCount);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "alpha")));
    }

    [Fact]
    public void Migrate_CreatesAdr0026Lanes_OrchestratorPrepAndNeedsHumanReview()
    {
        // ADR-0026 is purely additive: no rename, no migration of existing
        // jobs. The boot-time pass simply creates the two new lane folders
        // alongside the existing chain so the move endpoint can accept them
        // and the kanban can render them. Idempotent on a second call.
        var (machine, _) = BuildMachine();
        machine.EnsureStateFoldersAndMigrate();

        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.OrchestratorPrep)),
            "expected 1a-orchestrator-prep folder to be created on boot");
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.NeedsHumanReview)),
            "expected 1b-needs-human-review folder to be created on boot");

        // Existing lanes still present.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Preparation)));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready)));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview)));

        // Idempotent: calling again does not alter the workspace state.
        machine.EnsureStateFoldersAndMigrate();
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.OrchestratorPrep)));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.NeedsHumanReview)));
    }

    [Fact]
    public void Migrate_PreservesExistingTargetFolders_WithoutOverwriting()
    {
        // Existing 4-auto-review folder with a card that landed there
        // before the legacy 4-review lane was migrated. The migration
        // must not overwrite the existing folder; it skips and leaves the
        // legacy folder for manual reconciliation.
        Directory.CreateDirectory(Path.Combine(_watchPath, TaskStates.AutoReview));
        Directory.CreateDirectory(Path.Combine(_watchPath, TaskStates.AutoReview, "shared-slug"));
        File.WriteAllText(
            Path.Combine(_watchPath, TaskStates.AutoReview, "shared-slug", "marker.txt"),
            "new lane content");

        SeedLegacyJob("4-review", "shared-slug");

        var (machine, _) = BuildMachine();
        machine.EnsureStateFoldersAndMigrate();

        // The new-lane content is intact; the legacy folder is left in
        // place because the rename collided.
        Assert.True(File.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "shared-slug", "marker.txt")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, "4-review", "shared-slug")));
    }

    private void SeedLegacyJob(string laneFolder, string slug)
    {
        var dir = Path.Combine(_watchPath, laneFolder, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{laneFolder}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private void AssertMovedAndStateMatches(string newLane, string slug)
    {
        var dir = Path.Combine(_watchPath, newLane, slug);
        Assert.True(Directory.Exists(dir), $"expected {slug} under {newLane}");

        var json = File.ReadAllText(Path.Combine(dir, "job.json"));
        Assert.Contains($"\"state\": \"{newLane}\"", json);
    }

    private (TaskStateMachine, TaskScannerService) BuildMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        return (machine, scanner);
    }
}
