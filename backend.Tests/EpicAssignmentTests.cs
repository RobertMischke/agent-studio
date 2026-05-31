using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Epics foundation: card kind (task|epic) + the task-to-epic assignment ways.
///   Way 1 - at create time via CreateJobRequest.EpicId / Kind.
///   Way 2 - post-hoc via TaskMutationService.SetJobEpic (PUT /api/tasks/{id}/epic).
///   Way 3 - an epic's decomposition run reuses way 1 (create sub-tasks with EpicId).
/// Round-trips through job.json + the scanner so persistence is covered.
/// </summary>
public class EpicAssignmentTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "test-project";

    public EpicAssignmentTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "epic-assign-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TaskKinds_Normalize_DefaultsToTask_AndDetectsEpic()
    {
        Assert.Equal(TaskKinds.Task, TaskKinds.Normalize(null));
        Assert.Equal(TaskKinds.Task, TaskKinds.Normalize(""));
        Assert.Equal(TaskKinds.Task, TaskKinds.Normalize("nonsense"));
        Assert.Equal(TaskKinds.Epic, TaskKinds.Normalize("epic"));
        Assert.Equal(TaskKinds.Epic, TaskKinds.Normalize("EPIC"));
        Assert.True(TaskKinds.IsEpic("epic"));
        Assert.False(TaskKinds.IsEpic("task"));
    }

    [Fact]
    public void CreateJob_DefaultKindIsTask_NoEpicId()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateJobRequest { Id = "plain", Title = "Plain", WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("plain", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskKinds.Task, info!.Kind);
        Assert.Null(info.EpicId);
    }

    [Fact]
    public void CreateJob_WithKindEpic_IsPersistedAndScanned()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateJobRequest { Id = "the-epic", Title = "Epic", Kind = TaskKinds.Epic, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("the-epic", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskKinds.Epic, info!.Kind);
    }

    [Fact]
    public void CreateJob_WithEpicId_AssignsAtCreateTime_Way1()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateJobRequest { Id = "sub-1", Title = "Sub", EpicId = "the-epic", WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("sub-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("the-epic", info!.EpicId);
        Assert.Equal(TaskKinds.Task, info.Kind); // a sub-task is a task, not an epic
    }

    [Fact]
    public void SetJobEpic_AssignsAndDetaches_PostHoc_Way2()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateJobRequest { Id = "sub-2", Title = "Sub", WatchPath = _watchPath, TargetState = TaskStates.Ready });
        Assert.Null(scanner.FindJob("sub-2", _watchPath)!.EpicId);

        Assert.True(mutations.SetJobEpic("sub-2", "the-epic", _watchPath));
        Assert.Equal("the-epic", scanner.FindJob("sub-2", _watchPath)!.EpicId);

        // Detach with empty/null clears the link.
        Assert.True(mutations.SetJobEpic("sub-2", "", _watchPath));
        Assert.Null(scanner.FindJob("sub-2", _watchPath)!.EpicId);
    }

    [Fact]
    public void SetJobEpic_ReturnsFalse_ForUnknownTask()
    {
        var (_, _, mutations) = Build();
        Assert.False(mutations.SetJobEpic("does-not-exist", "the-epic", _watchPath));
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        machine.EnsureStateFoldersAndMigrate();
        return (machine, scanner, mutations);
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
