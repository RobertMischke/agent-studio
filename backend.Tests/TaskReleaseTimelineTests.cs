using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskReleaseTimelineTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "task-release-timeline-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetJobReleased_WritesOneAttributedTimelineEvent()
    {
        var watchPath = Path.Combine(_workspace, "projects", "demo");
        Directory.CreateDirectory(watchPath);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
            })
            .Build();
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance)
            .EnsureStateFoldersAndMigrate();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(watchPath, "demo", DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "release-target",
            Title = "Release target",
            WatchPath = watchPath,
            Agent = "codex",
        }) ?? throw new InvalidOperationException("Fixture task was not created.");
        var folder = scanner.FindJob(id, watchPath)!.FolderPath;

        Assert.True(mutations.SetJobReleased(id, true, watchPath, "human:operator-1"));
        Assert.True(mutations.SetJobReleased(id, true, watchPath, "human:operator-1"));

        var released = Assert.Single(
            timeline.ReadAll(folder), evt => evt.Kind == TimelineEventKinds.TaskReleased);
        Assert.Equal("human:operator-1", released.Actor);
        Assert.Equal("true", released.Details!["released"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* Best-effort cleanup for the isolated test workspace. */ }
    }
}
