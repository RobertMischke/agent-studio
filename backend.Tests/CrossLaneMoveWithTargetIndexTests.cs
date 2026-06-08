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
/// Regression coverage for the "cross-lane drag doesn't remember the
/// drop position" bug. The pre-fix path moved the folder, updated the
/// state field, and left the job's <c>order</c> field at whatever value
/// it carried over from the source lane. The next /api/tasks/grouped
/// poll then sorted by that stale order, so the dropped card snapped
/// to a position the user did not choose. The fix routes the desired
/// 0-based insertion slot through <see cref="TaskStateMachine.SetOrderInLane"/>,
/// which rewrites the entire target lane to a dense 1..N sequence with
/// the moved card pinned at the chosen slot.
/// </summary>
public class CrossLaneMoveWithTargetIndexTests : IDisposable
{
    private readonly string _watchPath;

    public CrossLaneMoveWithTargetIndexTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-xlane-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SetOrderInLane_DropAtTop_DraggedItemEndsAtOrderOne()
    {
        // Three jobs already in Ready, plus a fourth that was dragged in
        // from another lane. The cross-lane move has already happened on
        // disk; we now want the dropped job at slot 0 (the very top).
        WriteJob(TaskStates.Ready, "alpha", order: 5);
        WriteJob(TaskStates.Ready, "beta",  order: 6);
        WriteJob(TaskStates.Ready, "gamma", order: 7);
        WriteJob(TaskStates.Ready, "delta", order: 99); // freshly-moved card; stale source order

        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 0);

        Assert.True(ok);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.Equal(1, orders["delta"]);
        Assert.True(orders["delta"] < orders["alpha"]);
        Assert.True(orders["delta"] < orders["beta"]);
        Assert.True(orders["delta"] < orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_DropInMiddle_PreservesNeighbourBoundaries()
    {
        WriteJob(TaskStates.Ready, "alpha", order: 1);
        WriteJob(TaskStates.Ready, "beta",  order: 2);
        WriteJob(TaskStates.Ready, "gamma", order: 3);
        WriteJob(TaskStates.Ready, "delta", order: 99);

        // Drop delta at slot 2 -> sequence becomes alpha, beta, delta, gamma.
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 2);

        Assert.True(ok);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.True(orders["alpha"] < orders["beta"]);
        Assert.True(orders["beta"]  < orders["delta"]);
        Assert.True(orders["delta"] < orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_DropAtEnd_DraggedItemEndsAtLargestOrder()
    {
        WriteJob(TaskStates.Ready, "alpha", order: 1);
        WriteJob(TaskStates.Ready, "beta",  order: 2);
        WriteJob(TaskStates.Ready, "gamma", order: 3);
        WriteJob(TaskStates.Ready, "delta", order: 99);

        // Slot 3 = after the last existing sibling.
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 3);

        Assert.True(ok);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.True(orders["delta"] > orders["alpha"]);
        Assert.True(orders["delta"] > orders["beta"]);
        Assert.True(orders["delta"] > orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_ClampsOutOfRangeIndex()
    {
        WriteJob(TaskStates.Ready, "alpha", order: 1);
        WriteJob(TaskStates.Ready, "beta",  order: 2);
        WriteJob(TaskStates.Ready, "delta", order: 99);

        // 999 is well past the end - should clamp to "append at end".
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 999);

        Assert.True(ok);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.True(orders["delta"] > orders["alpha"]);
        Assert.True(orders["delta"] > orders["beta"]);
    }

    [Fact]
    public async Task MoveAsync_WithTargetIndex_PinsCrossLaneDropAtChosenSlot()
    {
        // Reproduces the user-visible bug end-to-end through the transition
        // service: alpha/beta/gamma in 2-ready, delta in 1-preparation
        // (carrying a high order value from a prior position). Drag delta
        // into 2-ready at slot 0; the moved card must end up at order 1.
        WriteJob(TaskStates.Ready, "alpha", order: 10);
        WriteJob(TaskStates.Ready, "beta",  order: 20);
        WriteJob(TaskStates.Ready, "gamma", order: 30);
        WriteJob(TaskStates.Preparation, "delta", order: 7);

        var transitions = BuildTransitionService();

        var outcome = await transitions.MoveAsync(
            "delta",
            TaskStates.Ready,
            _watchPath,
            CancellationToken.None,
            targetIndex: 0);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.True(orders.ContainsKey("delta"), "delta must be visible in 2-ready after the move");
        Assert.Equal(1, orders["delta"]);
        Assert.True(orders["delta"] < orders["alpha"]);
        Assert.True(orders["delta"] < orders["beta"]);
        Assert.True(orders["delta"] < orders["gamma"]);
    }

    [Fact]
    public async Task MoveAsync_WithoutTargetIndex_PreservesLegacyOrderBehaviour()
    {
        // Callers that omit targetIndex (auto-commit on progress -> auto-review,
        // archive-all sweeps, lane-dropdown moves) must not see their order
        // values rewritten by accident. The post-move order should be the
        // pre-move order: the folder moved, nothing else.
        WriteJob(TaskStates.Ready, "alpha", order: 10);
        WriteJob(TaskStates.Preparation, "delta", order: 7);

        var transitions = BuildTransitionService();

        var outcome = await transitions.MoveAsync(
            "delta",
            TaskStates.Ready,
            _watchPath,
            CancellationToken.None,
            targetIndex: null);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var orders = ReadOrders(TaskStates.Ready);
        Assert.Equal(7, orders["delta"]);
        Assert.Equal(10, orders["alpha"]);
    }

    private void WriteJob(string state, string slug, int order)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order},\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, int> ReadOrders(string state)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.ScanAllJobs()
            .Where(j => j.State == state)
            .ToDictionary(j => j.Id, j => j.Order);
    }

    private TaskStateMachine BuildMachine()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
    }

    private TaskTransitionService BuildTransitionService()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        return new TaskTransitionService(scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "xlane-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
