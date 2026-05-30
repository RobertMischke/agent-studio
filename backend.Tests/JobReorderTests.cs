using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Acceptance contract for the recurring "kanban lane reorder drop-on-top
/// must set order=1" bug. The frontend computes a new in-lane sequence
/// from a drag gesture and POSTs the ordered slug list to
/// <see cref="TaskStateMachine.ReorderJobs"/>; the backend rewrites every
/// job.json's <c>order</c> field to its 1-based position in the list.
///
/// The bug surfaced as "dragged card landed at order 2 instead of order
/// 1". The frontend-side hit-target fix is covered by
/// <c>frontend/e2e/kanban-reorder-drop-on-top.spec.ts</c>; these unit
/// tests pin the backend half of the contract: given the right ordered
/// list, the persisted <c>order</c> values are the expected dense 1..N
/// sequence, regardless of what numeric orders the lane started with.
/// </summary>
public class TaskReorderTests : IDisposable
{
    private readonly string _watchPath;

    public TaskReorderTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-reorder-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReorderJobs_DragBottomToTop_DraggedItemEndsAtOrderOne()
    {
        // Arrange — three Ready jobs at orders [10, 20, 30]. The starting
        // values are deliberately not 1..3 so a "happens to already be in
        // order" implementation can't fake the assertion.
        WriteReadyJob("alpha", order: 10);
        WriteReadyJob("beta",  order: 20);
        WriteReadyJob("gamma", order: 30);

        var machine = BuildMachine();

        // Act — frontend "drag gamma (bottom) to the top" gesture: gamma is
        // first in the new list, then alpha, then beta.
        var success = machine.ReorderJobs(new List<TaskOrderItem>
        {
            new() { JobId = "gamma", WatchPath = _watchPath },
            new() { JobId = "alpha", WatchPath = _watchPath },
            new() { JobId = "beta",  WatchPath = _watchPath },
        });

        // Assert — dense 1..N order, with the dragged item strictly smaller
        // than every other order in the lane.
        Assert.True(success);
        var orders = ReadOrders();
        Assert.Equal(1, orders["gamma"]);
        Assert.Equal(2, orders["alpha"]);
        Assert.Equal(3, orders["beta"]);
        Assert.True(orders["gamma"] < orders["alpha"], "drag-to-top: gamma must sort before alpha");
        Assert.True(orders["gamma"] < orders["beta"],  "drag-to-top: gamma must sort before beta");
    }

    [Fact]
    public void ReorderJobs_DragTopToBottom_DraggedItemEndsAtLargestOrder()
    {
        WriteReadyJob("alpha", order: 10);
        WriteReadyJob("beta",  order: 20);
        WriteReadyJob("gamma", order: 30);

        var machine = BuildMachine();

        // "Drag alpha to the bottom": new list is beta, gamma, alpha.
        var success = machine.ReorderJobs(new List<TaskOrderItem>
        {
            new() { JobId = "beta",  WatchPath = _watchPath },
            new() { JobId = "gamma", WatchPath = _watchPath },
            new() { JobId = "alpha", WatchPath = _watchPath },
        });

        Assert.True(success);
        var orders = ReadOrders();
        Assert.True(orders["alpha"] > orders["beta"],  "drag-to-bottom: alpha must sort after beta");
        Assert.True(orders["alpha"] > orders["gamma"], "drag-to-bottom: alpha must sort after gamma");
    }

    [Fact]
    public void ReorderJobs_DropBetweenTwoItems_PreservesNeighbourBoundaries()
    {
        WriteReadyJob("alpha", order: 10);
        WriteReadyJob("beta",  order: 20);
        WriteReadyJob("gamma", order: 30);
        WriteReadyJob("delta", order: 40);

        var machine = BuildMachine();

        // "Drag delta between alpha and beta": new list is alpha, delta, beta, gamma.
        var success = machine.ReorderJobs(new List<TaskOrderItem>
        {
            new() { JobId = "alpha", WatchPath = _watchPath },
            new() { JobId = "delta", WatchPath = _watchPath },
            new() { JobId = "beta",  WatchPath = _watchPath },
            new() { JobId = "gamma", WatchPath = _watchPath },
        });

        Assert.True(success);
        var orders = ReadOrders();
        Assert.True(orders["alpha"] < orders["delta"], "drop-between: delta must sort after alpha");
        Assert.True(orders["delta"] < orders["beta"],  "drop-between: delta must sort before beta");
    }

    private void WriteReadyJob(string slug, int order)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Ready}\",\"order\":{order},\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, int> ReadOrders()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.ScanAllJobs()
            .Where(j => j.State == TaskStates.Ready)
            .ToDictionary(j => j.Id, j => j.Order);
    }

    private TaskStateMachine BuildMachine()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "reorder-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
