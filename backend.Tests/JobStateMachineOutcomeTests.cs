using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// <see cref="JobStateMachine.MoveJob"/> must return the absolute path of the
/// post-move folder in <see cref="MoveJobOutcome.NewFolderPath"/> so callers
/// (chat-log writes, follow-up file emission) can target the moved folder
/// without round-tripping through the scanner cache. The cache may not yet
/// reflect the move when those callers run; falling back to the stale source
/// path resurrects 4-auto-review as a one-line skeleton (2026-05-16 incident).
/// </summary>
public class JobStateMachineOutcomeTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public JobStateMachineOutcomeTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-outcome-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in JobStates.All)
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
        SeedJob(JobStates.AutoReview, "promote-me");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("promote-me", JobStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotNull(outcome.NewFolderPath);
        Assert.Equal(
            Path.Combine(_watchPath, JobStates.HumanReview, "promote-me"),
            outcome.NewFolderPath);

        // The path the outcome reports actually exists on disk; the source path is gone.
        Assert.True(Directory.Exists(outcome.NewFolderPath));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "promote-me")));
    }

    [Fact]
    public void MoveJob_NoOpWhenAlreadyInTargetState_ReturnsCurrentFolderPath()
    {
        SeedJob(JobStates.HumanReview, "already-here");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("already-here", JobStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(
            Path.Combine(_watchPath, JobStates.HumanReview, "already-here"),
            outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_NotFound_NewFolderPathIsNull()
    {
        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("does-not-exist", JobStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
        Assert.Null(outcome.NewFolderPath);
    }

    [Fact]
    public void MoveJob_TargetFolderExists_NewFolderPathIsNull()
    {
        SeedJob(JobStates.AutoReview, "collide");
        SeedJob(JobStates.HumanReview, "collide");

        var (machine, _) = BuildMachine();
        var outcome = machine.MoveJob("collide", JobStates.HumanReview, _watchPath);

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

    private (JobStateMachine, JobScannerService) BuildMachine()
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
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var machine = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        return (machine, scanner);
    }
}
