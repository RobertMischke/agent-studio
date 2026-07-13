using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskWatcherServiceTests : IDisposable
{
    private readonly string _watchPath;
    private readonly TaskWatcherService _watcher;

    public TaskWatcherServiceTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_watchPath);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "watcher-test",
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _watcher = new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, config);
    }

    [Fact]
    public void TaskJsonHeartbeatOnlyChange_DoesNotInvalidateIndexAgain()
    {
        var taskJson = WriteTaskJson("original", DateTime.UtcNow);

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));

        WriteTaskJson("original", DateTime.UtcNow.AddSeconds(5));

        Assert.False(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));
    }

    [Fact]
    public void TaskJsonBoardFieldChange_InvalidatesIndex()
    {
        var taskJson = WriteTaskJson("original", DateTime.UtcNow);
        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));

        WriteTaskJson("renamed", DateTime.UtcNow.AddSeconds(5));

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, taskJson, WatcherChangeTypes.Changed));
    }

    [Theory]
    [InlineData("logs/cli-output.log")]
    [InlineData("results/report.md")]
    [InlineData("pipeline-execution.json")]
    [InlineData("post-processing-outcome.json")]
    public void GeneratedSidecarChange_DoesNotInvalidateTaskIndex(string relativePath)
    {
        var path = Path.Combine(_watchPath, TaskStates.Progress, "demo", relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.False(_watcher.ShouldNotifyIndexChange(
            _watchPath, path, WatcherChangeTypes.Changed));
    }

    [Fact]
    public void ExternalLegacyTaskFolderMove_IsIndexRelevant()
    {
        var oldPath = Path.Combine(_watchPath, TaskStates.Progress, "demo");
        var newPath = Path.Combine(_watchPath, TaskStates.Ready, "demo");

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, newPath, WatcherChangeTypes.Renamed, oldPath));
    }

    [Fact]
    public void ExternalFlatTaskFolderDelete_IsIndexRelevant()
    {
        var deletedPath = Path.Combine(_watchPath, TaskStorageLayout.JobsDirName, "000", "ATP-42");

        Assert.True(_watcher.ShouldNotifyIndexChange(
            _watchPath, deletedPath, WatcherChangeTypes.Deleted));
    }

    private string WriteTaskJson(string title, DateTime lastProgressAt)
    {
        var directory = Path.Combine(_watchPath, TaskStates.Progress, "demo");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "task.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            id = "demo",
            title,
            state = TaskStates.Progress,
            order = 1,
            lastProgressAt = lastProgressAt.ToString("O"),
        }));
        return path;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }
}
