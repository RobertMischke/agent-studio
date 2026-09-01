using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskMutationInvalidationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "task-mutation-invalidation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetJobPhase_InvalidatesOnlyWhenPhaseChanges_AndRefreshesProjection()
    {
        var watchPath = Path.Combine(_root, "projects", "demo");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(watchPath, state));
        var folder = Path.Combine(watchPath, TaskStates.Progress, "phase-task");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = "phase-task",
                title = "Phase task",
                state = TaskStates.Progress,
                phase = LifecyclePhases.ExecutionRunning,
                phaseEnteredAt = "2026-09-01T08:00:00.0000000Z",
                order = 1,
            }));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watchPath,
                ["TaskRepository"] = _root,
            })
            .Build();
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(cache);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        Assert.Equal(
            LifecyclePhases.ExecutionRunning,
            Assert.Single(scanner.ScanAllJobs()).Phase);
        var originalJson = File.ReadAllText(Path.Combine(folder, "task.json"));

        Assert.True(mutations.SetJobPhase(folder, LifecyclePhases.ExecutionRunning));

        Assert.Equal(0, cache.MutationInvalidations);
        Assert.Equal(originalJson, File.ReadAllText(Path.Combine(folder, "task.json")));

        Assert.True(mutations.SetJobPhase(folder, LifecyclePhases.LoopWaiting));

        Assert.Equal(1, cache.MutationInvalidations);
        var refreshed = Assert.Single(scanner.ScanAllJobs());
        Assert.Equal(LifecyclePhases.LoopWaiting, refreshed.Phase);
        Assert.NotNull(refreshed.PhaseEnteredAt);
        Assert.True(refreshed.PhaseEnteredAt > new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
