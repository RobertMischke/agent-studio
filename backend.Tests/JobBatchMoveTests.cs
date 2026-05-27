using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Acceptance contract for the batch-move endpoint. The 2026-05-08 manual
/// <c>mv</c> incident that produced the 2026-05-09 zombie folder happened
/// because there was no atomic batch path for "restore N jobs from
/// archive". This test pins the per-item-atomic contract: a conflict on
/// one item must not roll back items that already moved, and every item
/// gets a typed status string in the response.
/// </summary>
public class JobBatchMoveTests : IDisposable
{
    private readonly string _watchPath;

    public JobBatchMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-batchmove-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task BatchMoveAsync_FiveMovesAcrossThreeLanes_LandsEveryItemInTargetLane()
    {
        // Five archived jobs that we want to restore into three different
        // target lanes - the canonical "manual restore" gesture that used
        // to drop to shell mv.
        WriteJob(JobStates.Archive, "alpha");
        WriteJob(JobStates.Archive, "beta");
        WriteJob(JobStates.Archive, "gamma");
        WriteJob(JobStates.Archive, "delta");
        WriteJob(JobStates.Archive, "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = JobStates.Backlog },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = JobStates.Backlog },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = JobStates.Preparation },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("moved", r.Status));

        var laneByJob = ReadLaneByJob();
        Assert.Equal(JobStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(JobStates.Ready,       laneByJob["beta"]);
        Assert.Equal(JobStates.Backlog,     laneByJob["gamma"]);
        Assert.Equal(JobStates.Backlog,     laneByJob["delta"]);
        Assert.Equal(JobStates.Preparation, laneByJob["epsilon"]);
    }

    [Fact]
    public async Task BatchMoveAsync_ConflictOnItemThree_StillAppliesItemsOneTwoFourFive()
    {
        // Items 1, 2, 4, 5 come from archive and should move into 2-ready.
        // Item 3 (gamma) starts in 1-preparation but a stale folder with
        // the same slug already exists in 2-ready - that's the
        // TargetFolderExists case that surfaced the 409 conflict on the
        // single-item endpoint. The batch must keep going and report
        // conflict for gamma while items 1, 2, 4, 5 all land.
        WriteJob(JobStates.Archive,     "alpha");
        WriteJob(JobStates.Archive,     "beta");
        WriteJob(JobStates.Preparation, "gamma");
        WriteJob(JobStates.Ready,       "gamma");   // pre-existing stale duplicate
        WriteJob(JobStates.Archive,     "delta");
        WriteJob(JobStates.Archive,     "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = JobStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal("moved",    results[0].Status);
        Assert.Equal("moved",    results[1].Status);
        Assert.Equal("conflict", results[2].Status);
        Assert.Equal("moved",    results[3].Status);
        Assert.Equal("moved",    results[4].Status);
        Assert.False(string.IsNullOrEmpty(results[2].Message),
            "conflict result must carry the stale-duplicate message so the caller can surface it");

        // Folders 1, 2, 4, 5 must have landed in 2-ready. Item 3 must
        // still be in 1-preparation - the conflict prevented the move,
        // not "moved + rolled back".
        var folders = ReadFoldersByLane();
        Assert.Contains("alpha",   folders[JobStates.Ready]);
        Assert.Contains("beta",    folders[JobStates.Ready]);
        Assert.Contains("delta",   folders[JobStates.Ready]);
        Assert.Contains("epsilon", folders[JobStates.Ready]);
        Assert.Contains("gamma",   folders[JobStates.Preparation]);
    }

    [Fact]
    public async Task BatchMoveAsync_InvalidLane_ReportsRejectedWithoutBlockingOtherItems()
    {
        WriteJob(JobStates.Archive, "alpha");
        WriteJob(JobStates.Archive, "beta");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = "not-a-real-lane" },
            new() { JobId = "beta",  WatchPath = _watchPath, TargetState = JobStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("rejected", results[0].Status);
        Assert.Equal("moved",    results[1].Status);
    }

    [Fact]
    public async Task BatchMoveAsync_UnknownJob_ReportsNotFoundWithoutBlockingOtherItems()
    {
        WriteJob(JobStates.Archive, "alpha");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "ghost", WatchPath = _watchPath, TargetState = JobStates.Ready },
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = JobStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("not-found", results[0].Status);
        Assert.Equal("moved",     results[1].Status);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":10,\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, string> ReadLaneByJob()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        return scanner.ScanAllJobs().ToDictionary(j => j.Id, j => j.State);
    }

    private Dictionary<string, HashSet<string>> ReadFoldersByLane()
    {
        var byLane = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var state in JobStates.All)
        {
            var laneDir = Path.Combine(_watchPath, state);
            byLane[state] = new HashSet<string>(
                Directory.EnumerateDirectories(laneDir).Select(Path.GetFileName)!,
                StringComparer.Ordinal);
        }
        return byLane;
    }

    private JobTransitionService BuildTransitionService()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), NullLogger<JobMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        return new JobTransitionService(scanner, states, mutations, git, settings,
            NullLogger<JobTransitionService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "batchmove-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
