using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the failure-mode contract for <see cref="JobStateMachine.MoveJob"/>.
/// The pre-existing-target-folder case used to surface as a generic 404 in the UI;
/// it must now return <c>TargetFolderExists</c> so the endpoint can map it to 409
/// with a message that points at the stale duplicate.
/// </summary>
public class JobScannerMoveTests : IDisposable
{
    private readonly string _watchPath;

    public JobScannerMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private JobStateMachine BuildStateMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        return new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
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
        WriteJob(JobStates.Completed, "demo-task");

        var outcome = BuildStateMachine().MoveJob("demo-task", JobStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Archive, "demo-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Completed, "demo-task")));
    }

    [Fact]
    public void MoveJob_UnknownId_ReturnsNotFound()
    {
        var outcome = BuildStateMachine().MoveJob("ghost", JobStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void MoveJob_TargetFolderAlreadyExists_ReturnsTargetFolderExists()
    {
        WriteJob(JobStates.Completed, "duplicate-slug");
        WriteJob(JobStates.Archive, "duplicate-slug");

        var outcome = BuildStateMachine().MoveJob("duplicate-slug", JobStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.TargetFolderExists, outcome.Status);
        Assert.NotNull(outcome.Message);
        Assert.Contains("duplicate-slug", outcome.Message);
        Assert.Contains(JobStates.Archive, outcome.Message);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Completed, "duplicate-slug")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Archive, "duplicate-slug")));
    }
}
