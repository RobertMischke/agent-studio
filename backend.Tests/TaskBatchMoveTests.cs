using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// Acceptance contract for the batch-move endpoint. The 2026-05-08 manual
/// <c>mv</c> incident that produced the 2026-05-09 zombie folder happened
/// because there was no atomic batch path for "restore N jobs from
/// archive". This test pins the per-item-atomic contract: a conflict on
/// one item must not roll back items that already moved, and every item
/// gets a typed status string in the response.
/// </summary>
public class TaskBatchMoveTests : IDisposable
{
    private readonly string _watchPath;

    public TaskBatchMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-batchmove-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
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
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");
        WriteJob(TaskStates.Archive, "gamma");
        WriteJob(TaskStates.Archive, "delta");
        WriteJob(TaskStates.Archive, "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Preparation },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("moved", r.Status));

        var laneByJob = ReadLaneByJob();
        Assert.Equal(TaskStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(TaskStates.Ready,       laneByJob["beta"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["gamma"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["delta"]);
        Assert.Equal(TaskStates.Preparation, laneByJob["epsilon"]);
    }

    [Fact]
    public async Task JobsBatchMoveEndpoint_FiveMovesAcrossThreeLanes_ReturnsOrderedPerItemResults()
    {
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");
        WriteJob(TaskStates.Archive, "gamma");
        WriteJob(TaskStates.Archive, "delta");
        WriteJob(TaskStates.Archive, "epsilon");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "batchmove-test",
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath
                    });
                });
            });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs/batch-move")
        {
            Content = JsonContent.Create(new BatchMoveRequest
            {
                Items =
                [
                    new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
                    new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
                    new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
                    new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
                    new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Preparation },
                ]
            })
        };
        request.Headers.Add("X-Client-Id", "local-default");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchMoveResponse>();

        Assert.NotNull(body);
        Assert.Equal(["alpha", "beta", "gamma", "delta", "epsilon"], body!.Results.Select(r => r.JobId).ToArray());
        Assert.All(body.Results, r => Assert.Equal("moved", r.Status));

        var laneByJob = ReadLaneByJob();
        Assert.Equal(TaskStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(TaskStates.Ready,       laneByJob["beta"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["gamma"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["delta"]);
        Assert.Equal(TaskStates.Preparation, laneByJob["epsilon"]);
    }

    [Fact]
    public async Task BatchMoveAsync_TargetSlugCollision_AutoSuffixesAndStillMovesEveryItem()
    {
        // Items 1, 2, 4, 5 come from archive and should move into 2-ready.
        // Item 3 (gamma) starts in 1-preparation but a stale folder with the
        // same slug already exists in 2-ready - the case that used to surface
        // a 409 conflict on the single-item endpoint and strand the batch.
        // With the collision-safe move (Layer 2) every item moves: gamma is
        // auto-suffixed into 2-ready and the pre-existing namesake is left
        // untouched.
        WriteJob(TaskStates.Archive,     "alpha");
        WriteJob(TaskStates.Archive,     "beta");
        WriteJob(TaskStates.Preparation, "gamma");
        WriteJob(TaskStates.Ready,       "gamma");   // pre-existing stale duplicate
        WriteJob(TaskStates.Archive,     "delta");
        WriteJob(TaskStates.Archive,     "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("moved", r.Status));

        // alpha/beta/delta/epsilon land under their own slug; gamma's move
        // collided on the namesake so it landed as gamma-2. The 1-preparation
        // source is drained and the stale namesake in 2-ready is preserved.
        var folders = ReadFoldersByLane();
        Assert.Contains("alpha",   folders[TaskStates.Ready]);
        Assert.Contains("beta",    folders[TaskStates.Ready]);
        Assert.Contains("delta",   folders[TaskStates.Ready]);
        Assert.Contains("epsilon", folders[TaskStates.Ready]);
        Assert.Contains("gamma",   folders[TaskStates.Ready]);
        Assert.Contains("gamma-2", folders[TaskStates.Ready]);
        Assert.DoesNotContain("gamma", folders[TaskStates.Preparation]);
    }

    [Fact]
    public async Task BatchMoveAsync_InvalidLane_ReportsRejectedWithoutBlockingOtherItems()
    {
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = "not-a-real-lane" },
            new() { JobId = "beta",  WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("rejected", results[0].Status);
        Assert.Equal("moved",    results[1].Status);
    }

    [Fact]
    public async Task BatchMoveAsync_UnknownJob_ReportsNotFoundWithoutBlockingOtherItems()
    {
        WriteJob(TaskStates.Archive, "alpha");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "ghost", WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("not-found", results[0].Status);
        Assert.Equal("moved",     results[1].Status);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":10,\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, string> ReadLaneByJob()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.ScanAllJobs().ToDictionary(j => j.Id, j => j.State);
    }

    private Dictionary<string, HashSet<string>> ReadFoldersByLane()
    {
        var byLane = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var state in TaskStates.All)
        {
            var laneDir = Path.Combine(_watchPath, state);
            byLane[state] = new HashSet<string>(
                Directory.EnumerateDirectories(laneDir).Select(Path.GetFileName)!,
                StringComparer.Ordinal);
        }
        return byLane;
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
            ["WatchPaths:0:Name"] = "batchmove-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
