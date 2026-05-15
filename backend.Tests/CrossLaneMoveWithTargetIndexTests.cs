using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression coverage for the "cross-lane drag doesn't remember the
/// drop position" bug. The pre-fix path moved the folder, updated the
/// state field, and left the job's <c>order</c> field at whatever value
/// it carried over from the source lane. The next /api/jobs/grouped
/// poll then sorted by that stale order, so the dropped card snapped
/// to a position the user did not choose. The fix routes the desired
/// 0-based insertion slot through <see cref="JobStateMachine.SetOrderInLane"/>,
/// which rewrites the entire target lane to a dense 1..N sequence with
/// the moved card pinned at the chosen slot.
/// </summary>
public class CrossLaneMoveWithTargetIndexTests : IDisposable
{
    private readonly string _watchPath;

    public CrossLaneMoveWithTargetIndexTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-xlane-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
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
        WriteJob(JobStates.Ready, "alpha", order: 5);
        WriteJob(JobStates.Ready, "beta",  order: 6);
        WriteJob(JobStates.Ready, "gamma", order: 7);
        WriteJob(JobStates.Ready, "delta", order: 99); // freshly-moved card; stale source order

        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 0);

        Assert.True(ok);
        var orders = ReadOrders(JobStates.Ready);
        Assert.Equal(1, orders["delta"]);
        Assert.True(orders["delta"] < orders["alpha"]);
        Assert.True(orders["delta"] < orders["beta"]);
        Assert.True(orders["delta"] < orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_DropInMiddle_PreservesNeighbourBoundaries()
    {
        WriteJob(JobStates.Ready, "alpha", order: 1);
        WriteJob(JobStates.Ready, "beta",  order: 2);
        WriteJob(JobStates.Ready, "gamma", order: 3);
        WriteJob(JobStates.Ready, "delta", order: 99);

        // Drop delta at slot 2 -> sequence becomes alpha, beta, delta, gamma.
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 2);

        Assert.True(ok);
        var orders = ReadOrders(JobStates.Ready);
        Assert.True(orders["alpha"] < orders["beta"]);
        Assert.True(orders["beta"]  < orders["delta"]);
        Assert.True(orders["delta"] < orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_DropAtEnd_DraggedItemEndsAtLargestOrder()
    {
        WriteJob(JobStates.Ready, "alpha", order: 1);
        WriteJob(JobStates.Ready, "beta",  order: 2);
        WriteJob(JobStates.Ready, "gamma", order: 3);
        WriteJob(JobStates.Ready, "delta", order: 99);

        // Slot 3 = after the last existing sibling.
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 3);

        Assert.True(ok);
        var orders = ReadOrders(JobStates.Ready);
        Assert.True(orders["delta"] > orders["alpha"]);
        Assert.True(orders["delta"] > orders["beta"]);
        Assert.True(orders["delta"] > orders["gamma"]);
    }

    [Fact]
    public void SetOrderInLane_ClampsOutOfRangeIndex()
    {
        WriteJob(JobStates.Ready, "alpha", order: 1);
        WriteJob(JobStates.Ready, "beta",  order: 2);
        WriteJob(JobStates.Ready, "delta", order: 99);

        // 999 is well past the end - should clamp to "append at end".
        var ok = BuildMachine().SetOrderInLane("delta", _watchPath, targetIndex: 999);

        Assert.True(ok);
        var orders = ReadOrders(JobStates.Ready);
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
        WriteJob(JobStates.Ready, "alpha", order: 10);
        WriteJob(JobStates.Ready, "beta",  order: 20);
        WriteJob(JobStates.Ready, "gamma", order: 30);
        WriteJob(JobStates.Preparation, "delta", order: 7);

        var transitions = BuildTransitionService();

        var outcome = await transitions.MoveAsync(
            "delta",
            JobStates.Ready,
            _watchPath,
            CancellationToken.None,
            targetIndex: 0);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var orders = ReadOrders(JobStates.Ready);
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
        WriteJob(JobStates.Ready, "alpha", order: 10);
        WriteJob(JobStates.Preparation, "delta", order: 7);

        var transitions = BuildTransitionService();

        var outcome = await transitions.MoveAsync(
            "delta",
            JobStates.Ready,
            _watchPath,
            CancellationToken.None,
            targetIndex: null);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var orders = ReadOrders(JobStates.Ready);
        Assert.Equal(7, orders["delta"]);
        Assert.Equal(10, orders["alpha"]);
    }

    private void WriteJob(string state, string slug, int order)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":{order},\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, int> ReadOrders(string state)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        return scanner.ScanAllJobs()
            .Where(j => j.State == state)
            .ToDictionary(j => j.Id, j => j.Order);
    }

    private JobStateMachine BuildMachine()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        return new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
    }

    private JobTransitionService BuildTransitionService()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        return new JobTransitionService(scanner, states, mutations, git, settings,
            NullLogger<JobTransitionService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "xlane-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
