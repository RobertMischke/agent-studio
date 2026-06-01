using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the failure-mode contract for <see cref="TaskStateMachine.MoveJob"/>.
/// The pre-existing-target-folder case used to surface as a generic 404 in the UI;
/// it must now return <c>TargetFolderExists</c> so the endpoint can map it to 409
/// with a message that points at the stale duplicate.
/// </summary>
public class TaskScannerMoveTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskStateMachine BuildStateMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    [Fact]
    public void MoveJob_HappyPath_ReturnsSuccess()
    {
        WriteJob(TaskStates.Completed, "demo-task");

        var outcome = BuildStateMachine().MoveJob("demo-task", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "demo-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "demo-task")));
    }

    [Fact]
    public void MoveJob_UnknownId_ReturnsNotFound()
    {
        var outcome = BuildStateMachine().MoveJob("ghost", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void MoveJob_TargetFolderAlreadyExists_ReturnsTargetFolderExists()
    {
        WriteJob(TaskStates.Completed, "duplicate-slug");
        WriteJob(TaskStates.Archive, "duplicate-slug");

        var outcome = BuildStateMachine().MoveJob("duplicate-slug", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.TargetFolderExists, outcome.Status);
        Assert.NotNull(outcome.Message);
        Assert.Contains("duplicate-slug", outcome.Message);
        Assert.Contains(TaskStates.Archive, outcome.Message);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "duplicate-slug")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug")));
    }
}
