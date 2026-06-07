using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.TaskAccess;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Acceptance tests for the phase 2-3 <see cref="TaskAccessService"/>:
/// the read surface bottoms out in the in-memory index, mutations land
/// in both index and disk, and the lane-folder escape hatches
/// (SlugExistsInLane, ListLaneFolderNames, MoveOrphanToFailedPickup,
/// DeleteLaneFolder) hide the lane path shape from outside callers.
/// </summary>
public class TaskAccessServiceTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskAccessServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-task-access-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Reads_AfterCreate_FindAndListByLaneReturnTheJob()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var jobId = mutations.CreateJob(new CreateJobRequest
        {
            Id = "alpha",
            Title = "Alpha task",
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready,
            WatchPath = _watchPath,
        });
        Assert.Equal("alpha", jobId);

        var found = taskAccess.FindJob("alpha", _watchPath);
        Assert.NotNull(found);
        Assert.Equal(TaskStates.Ready, found!.State);

        var inLane = taskAccess.ListByLane(Project, TaskStates.Ready);
        Assert.Single(inLane);
        Assert.Equal("alpha", inLane[0].Id);

        var inWorkspace = taskAccess.ListByLaneInWorkspace(_watchPath, TaskStates.Ready);
        Assert.Single(inWorkspace);

        var inProject = taskAccess.ListByProject(Project);
        Assert.Single(inProject);
    }

    [Fact]
    public async Task MutateAsync_Create_RoundtripsThroughLayerAndFiresSubscriber()
    {
        var (taskAccess, machine, _, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var received = new List<TaskChange>();
        using var sub = taskAccess.Subscribe(Project, c => received.Add(c));

        var result = await taskAccess.MutateAsync(new TaskMutationRequest
        {
            Kind = TaskMutationKind.Create,
            CreateRequest = new CreateJobRequest
            {
                Id = "beta",
                Title = "Beta task",
                Agent = "claude",
                TargetState = TaskStates.Preparation,
                WatchPath = _watchPath,
            },
        });

        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        Assert.Equal("beta", result.Job!.Id);
        Assert.Equal(TaskStates.Preparation, result.Job.State);
        Assert.Single(received);
        Assert.Equal(TaskChangeKind.Created, received[0].Kind);
        Assert.Equal("beta", received[0].JobId);
        Assert.Equal(TaskStates.Preparation, received[0].ToLane);
    }

    [Fact]
    public async Task MutateAsync_UpdateField_BumpsVersionAndDispatches()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateJobRequest
        {
            Id = "gamma",
            Title = "Gamma task",
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready,
            WatchPath = _watchPath,
        });

        var received = new List<TaskChange>();
        using var sub = taskAccess.Subscribe(Project, c => received.Add(c));

        var result = await taskAccess.MutateAsync(new TaskMutationRequest
        {
            JobId = "gamma",
            WatchPath = _watchPath,
            Kind = TaskMutationKind.UpdateField,
            FieldName = "title",
            FieldValue = "Gamma renamed",
        });

        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        Assert.Equal("Gamma renamed", taskAccess.FindJob("gamma", _watchPath)!.Title);
        Assert.True(result.Version!.Version >= 1);
        Assert.Single(received);
        Assert.Equal(TaskChangeKind.Updated, received[0].Kind);
    }

    [Fact]
    public async Task MutateAsync_StaleVersion_ReturnsConflict()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateJobRequest
        {
            Id = "delta",
            Title = "Delta",
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready,
            WatchPath = _watchPath,
        });

        // First mutation establishes version 1 on this job.
        await taskAccess.MutateAsync(new TaskMutationRequest
        {
            JobId = "delta",
            WatchPath = _watchPath,
            Kind = TaskMutationKind.UpdateField,
            FieldName = "title",
            FieldValue = "Delta one",
        });

        // Second mutation with an explicitly stale expected version should
        // be rejected with Conflict.
        var staleVersion = new TaskAccessVersion(0, DateTime.UtcNow);
        var conflict = await taskAccess.MutateAsync(new TaskMutationRequest
        {
            JobId = "delta",
            WatchPath = _watchPath,
            Kind = TaskMutationKind.UpdateField,
            FieldName = "title",
            FieldValue = "Delta two",
            ExpectedVersion = staleVersion,
        });
        Assert.Equal(TaskMutationStatus.Conflict, conflict.Status);
        Assert.Equal("Delta one", taskAccess.FindJob("delta", _watchPath)!.Title);
    }

    [Fact]
    public async Task TransitionLaneAsync_MovesAndDispatchesChange()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateJobRequest
        {
            Id = "epsilon",
            Title = "Epsilon",
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready,
            WatchPath = _watchPath,
        });

        var received = new List<TaskChange>();
        using var sub = taskAccess.Subscribe(Project, c => received.Add(c));

        var result = await taskAccess.TransitionLaneAsync(new TaskTransitionRequest
        {
            JobId = "epsilon",
            WatchPath = _watchPath,
            TargetLane = TaskStates.Progress,
        });
        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        Assert.Equal(TaskStates.Progress, taskAccess.FindJob("epsilon", _watchPath)!.State);
        Assert.Contains(received, c => c.Kind == TaskChangeKind.Transitioned && c.ToLane == TaskStates.Progress);
    }

    [Fact]
    public void SlugExistsInLane_ReturnsTrueOnlyForPhysicalLaneFolder()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateJobRequest
        {
            Id = "zeta",
            Title = "Zeta",
            Agent = "claude",
            TargetState = TaskStates.Ready,
            WatchPath = _watchPath,
        });

        Assert.False(taskAccess.SlugExistsInLane(_watchPath, TaskStates.Ready, "zeta"));
        Directory.CreateDirectory(Path.Combine(_watchPath, TaskStates.Ready, "orphan-zeta"));
        Assert.True(taskAccess.SlugExistsInLane(_watchPath, TaskStates.Ready, "orphan-zeta"));
        Assert.False(taskAccess.SlugExistsInLane(_watchPath, TaskStates.Progress, "zeta"));
        Assert.False(taskAccess.SlugExistsInLane(_watchPath, "not-a-lane", "zeta"));
    }

    [Fact]
    public void ListLaneFolderNames_IncludesFoldersWithoutJobJson()
    {
        var (taskAccess, machine, _, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // Synthesize a folder with no job.json under 3-progress. This
        // simulates the orphan case the migration-target consumers rely
        // on the layer to enumerate.
        var orphanPath = Path.Combine(_watchPath, TaskStates.Progress, "orphan-folder");
        Directory.CreateDirectory(orphanPath);

        var names = taskAccess.ListLaneFolderNames(_watchPath, TaskStates.Progress);
        Assert.Contains("orphan-folder", names);
    }

    [Fact]
    public void MoveOrphanToFailedPickup_MovesAndWritesReason()
    {
        var (taskAccess, machine, _, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var orphanPath = Path.Combine(_watchPath, TaskStates.Progress, "stale-folder");
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "logs.txt"), "some log");

        var result = taskAccess.MoveOrphanToFailedPickup(
            _watchPath,
            TaskStates.Progress,
            "stale-folder",
            "stale-folder-orphan-2026-05-15",
            "# Test reason\n");

        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        Assert.False(Directory.Exists(orphanPath));
        var destination = Path.Combine(_watchPath, TaskStates.FailedPickup, "stale-folder-orphan-2026-05-15");
        Assert.True(Directory.Exists(destination));
        Assert.Contains("Test reason", File.ReadAllText(Path.Combine(destination, "failed-pickup-reason.md")));
    }

    [Fact]
    public void DeleteLaneFolder_RemovesFolder()
    {
        var (taskAccess, machine, _, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var folder = Path.Combine(_watchPath, TaskStates.Progress, "skeleton");
        Directory.CreateDirectory(folder);

        var result = taskAccess.DeleteLaneFolder(_watchPath, TaskStates.Progress, "skeleton");
        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Snapshot_ReturnsConsistentJobList()
    {
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        for (int i = 0; i < 5; i++)
        {
            mutations.CreateJob(new CreateJobRequest
            {
                Id = $"job-{i}",
                Title = $"Job {i}",
                Agent = "claude",
                TargetState = TaskStates.Ready,
                WatchPath = _watchPath,
            });
        }
        var snap = taskAccess.Snapshot();
        Assert.Equal(5, snap.Jobs.Count);
    }

    [Fact]
    public void Reads_Under200Jobs_FinishUnderOneSecond()
    {
        // Phase 2 performance gate from the task prompt: index can serve
        // FindJob in well under a second across a 200-job board. Reads
        // already bottom out in TaskIndexCache, so this is mostly a
        // regression guard against a regression that would re-route
        // through a disk walk.
        var (taskAccess, machine, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        for (int i = 0; i < 200; i++)
        {
            mutations.CreateJob(new CreateJobRequest
            {
                Id = $"job-{i:000}",
                Title = $"Job {i}",
                Agent = "claude",
                TargetState = TaskStates.Ready,
                WatchPath = _watchPath,
            });
        }
        // Warm the cache once so the timed reads measure index hits, not
        // the first cold disk walk.
        _ = taskAccess.Snapshot();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            var info = taskAccess.FindJob($"job-{i:000}", _watchPath);
            Assert.NotNull(info);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"200 FindJob calls took {sw.ElapsedMilliseconds} ms");
    }

    private (TaskAccessService taskAccess, TaskStateMachine machine, TaskMutationService mutations, TaskIndexCache cache) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var transitions = new TaskTransitionService(scanner, machine, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var taskAccess = new TaskAccessService(scanner, mutations, machine, transitions, indexCache, NullLogger<TaskAccessService>.Instance);
        return (taskAccess, machine, mutations, indexCache);
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
