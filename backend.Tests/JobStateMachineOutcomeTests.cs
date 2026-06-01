using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// <see cref="TaskStateMachine.MoveJob"/> must return the absolute path of the
/// post-move folder in <see cref="MoveJobOutcome.NewFolderPath"/> so callers
/// (chat-log writes, follow-up file emission) can target the moved folder
/// without round-tripping through the scanner cache. The cache may not yet
/// reflect the move when those callers run; falling back to the stale source
/// path resurrects 4-auto-review as a one-line skeleton (2026-05-16 incident).
/// </summary>
public class TaskStateMachineOutcomeTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskStateMachineOutcomeTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-outcome-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MoveJob_Success_ReturnsAuthoritativeNewFolderPath()
    {
        SeedJob(TaskStates.AutoReview, "promote-me");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("promote-me", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.HumanReview, "promote-me"),
            outcome.NewFolderPath);

        // The path the outcome reports actually exists on disk; the source path is gone.
        Assert.True(Directory.Exists(outcome.NewFolderPath));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "promote-me")));
    }

    [Fact]
    public void MoveJob_NoOpWhenAlreadyInTargetState_ReturnsCurrentFolderPath()
    {
        SeedJob(TaskStates.HumanReview, "already-here");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("already-here", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.HumanReview, "already-here"),
            outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_NotFound_NewFolderPathIsNull()
    {
        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("does-not-exist", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
        Assert.Null(outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_TargetFolderExists_NewFolderPathIsNull()
    {
        SeedJob(TaskStates.AutoReview, "collide");
        SeedJob(TaskStates.HumanReview, "collide");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("collide", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.TargetFolderExists, outcome.Status);
        Assert.Null(outcome.NewFolderPath);
    }

    private void SeedJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private (TaskStateMachine, TaskScannerService) BuildMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        return (machine, scanner);
    }
}
