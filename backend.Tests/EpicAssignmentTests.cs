using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Epics foundation: card kind (task|epic) + the task-to-epic assignment ways.
///   Way 1 - at create time via CreateTaskRequest.EpicId / Kind.
///   Way 2 - post-hoc via TaskMutationService.SetJobEpic (PUT /api/tasks/{id}/epic).
///   Way 3 - an epic's decomposition run reuses way 1 (create sub-tasks with EpicId).
/// Round-trips through task.json + the scanner so persistence is covered.
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
        mutations.CreateJob(new CreateTaskRequest { Id = "plain", Title = "Plain", WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("plain", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskKinds.Task, info!.Kind);
        Assert.Null(info.EpicId);
    }

    [Fact]
    public void CreateJob_WithKindEpic_IsPersistedAndScanned()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "the-epic", Title = "Epic", Kind = TaskKinds.Epic, WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("the-epic", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskKinds.Epic, info!.Kind);
    }

    [Fact]
    public void CreateJob_WithEpicId_AssignsAtCreateTime_Way1()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "sub-1", Title = "Sub", EpicId = "the-epic", WatchPath = _watchPath, TargetState = TaskStates.Ready });

        var info = scanner.FindJob("sub-1", _watchPath);
        Assert.NotNull(info);
        Assert.Equal("the-epic", info!.EpicId);
        Assert.Equal(TaskKinds.Task, info.Kind); // a sub-task is a task, not an epic
    }

    [Fact]
    public void SetJobEpic_AssignsAndDetaches_PostHoc_Way2()
    {
        var (_, scanner, mutations) = Build();
        mutations.CreateJob(new CreateTaskRequest { Id = "sub-2", Title = "Sub", WatchPath = _watchPath, TargetState = TaskStates.Ready });
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

    // BuildRollup is pure (TaskInfo epic, all-tasks) -> rollup, so it is tested
    // with hand-built TaskInfo lists rather than the create path: CreateJob only
    // lands jobs in backlog/preparation/ready, which cannot exercise the
    // completed/in-progress buckets.
    private static TaskInfo Sub(string id, string epicId, string state, int order) =>
        new() { Id = id, Title = id, Kind = TaskKinds.Task, EpicId = epicId, State = state, Order = order };

    [Fact]
    public void BuildRollup_EmptyEpic_HasZeroProgress()
    {
        var epic = new TaskInfo { Id = "epic-a", Key = "ASS-1", Title = "Epic A", Kind = TaskKinds.Epic, State = TaskStates.Ready };
        var rollup = EpicEndpoints.BuildRollup(epic, new List<TaskInfo> { epic });

        Assert.Equal("epic-a", rollup.Id);
        Assert.Equal("ASS-1", rollup.Key);
        Assert.Equal(0, rollup.SubTaskTotal);
        Assert.Equal(0, rollup.Completed);
        Assert.Equal(0, rollup.InProgress);
        Assert.Equal(0, rollup.Open);
        Assert.Empty(rollup.SubTasks);
    }

    [Fact]
    public void BuildRollup_BucketsSubTasksByLane()
    {
        var epic = new TaskInfo { Id = "epic-b", Title = "Epic B", Kind = TaskKinds.Epic, State = TaskStates.Ready };
        var all = new List<TaskInfo>
        {
            epic,
            Sub("s-backlog", "epic-b", TaskStates.Backlog, 1),
            Sub("s-ready", "epic-b", TaskStates.Ready, 2),
            Sub("s-prog", "epic-b", TaskStates.Progress, 3),
            Sub("s-done", "epic-b", TaskStates.Completed, 4),
        };

        var rollup = EpicEndpoints.BuildRollup(epic, all);

        Assert.Equal(4, rollup.SubTaskTotal);
        Assert.Equal(1, rollup.Completed);                       // s-done
        Assert.Equal(2, rollup.Open);                            // s-backlog + s-ready
        Assert.Equal(1, rollup.InProgress);                      // s-prog
        Assert.Equal(4, rollup.SubTasks.Count);
        Assert.Equal(1, rollup.ByState[TaskStates.Progress]);    // raw per-lane count preserved
        Assert.Equal("s-backlog", rollup.SubTasks[0].Id);        // ordered by Order
    }

    [Fact]
    public void BuildRollup_CarriesSubTaskOrchestratorVerdict()
    {
        var epic = new TaskInfo { Id = "epic-verdict", Title = "Epic", Kind = TaskKinds.Epic, State = TaskStates.Ready };
        var all = new List<TaskInfo>
        {
            epic,
            Sub("s-review", "epic-verdict", TaskStates.HumanReview, 1) with { OrchestratorVerdict = "escalate" },
        };

        var rollup = EpicEndpoints.BuildRollup(epic, all);

        Assert.Equal("escalate", rollup.SubTasks[0].OrchestratorVerdict);
    }

    [Fact]
    public void BuildRollup_IgnoresOtherEpicsSubTasks()
    {
        var epicC = new TaskInfo { Id = "epic-c", Title = "Epic C", Kind = TaskKinds.Epic, State = TaskStates.Ready };
        var all = new List<TaskInfo>
        {
            epicC,
            new TaskInfo { Id = "epic-d", Title = "Epic D", Kind = TaskKinds.Epic, State = TaskStates.Ready },
            Sub("owned", "epic-c", TaskStates.Ready, 1),
            Sub("foreign", "epic-d", TaskStates.Ready, 1),
        };

        var rollupC = EpicEndpoints.BuildRollup(epicC, all);

        Assert.Equal(1, rollupC.SubTaskTotal);
        Assert.Equal("owned", rollupC.SubTasks[0].Id);
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
