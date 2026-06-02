using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the run-status -> CORE pipeline step status mapping.
///
/// The bug this guards against: <c>ProjectRunner.RecordCoreRunFinish</c> used
/// to mark the CORE "Agent execution" step Passed only when
/// <c>status == "completed" AND exitCode is null or 0</c>. But a
/// sentinel-detected or silent-completion run - the deterministic happy path
/// for Claude / Codex - is killed to release the lingering process, and
/// <c>Process.Kill</c> hands back <c>exitCode = -1</c> on Windows. The
/// <see cref="RunStatusClassifier"/> already settles that into
/// <see cref="RunStatuses.Completed"/>, but the extra exit-code gate flipped
/// the CORE step back to Failed, so it never showed as completed in the
/// Overview pipeline table even though the run had ended cleanly.
/// </summary>
public class CoreRunStepStatusMapperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(137)]
    public void CompletedStatus_AnyExitCode_IsPassed(int? exitCode)
    {
        // The headline regression: a sentinel-killed completion reports
        // status="completed" with exitCode=-1 on Windows. The CORE step must
        // be Passed regardless of the kill-induced exit code.
        var execution = new CliExecution { Status = RunStatuses.Completed, ExitCode = exitCode };

        Assert.Equal(PipelineStepStatus.Passed, CoreRunStepStatusMapper.From(execution));
    }

    [Theory]
    [InlineData(RunStatuses.Failed)]
    [InlineData(RunStatuses.Stopped)]
    [InlineData("cancelled")]
    [InlineData("running")]
    [InlineData("")]
    public void NonCompletedStatus_IsFailed(string status)
    {
        // Anything the classifier did not call "completed" is not a successful
        // CORE run, so the step is Failed - even with a zero exit code.
        var execution = new CliExecution { Status = status, ExitCode = 0 };

        Assert.Equal(PipelineStepStatus.Failed, CoreRunStepStatusMapper.From(execution));
    }

    [Fact]
    public void StatusComparison_IsCaseInsensitive()
    {
        Assert.Equal(PipelineStepStatus.Passed, CoreRunStepStatusMapper.From("COMPLETED"));
    }

    [Fact]
    public void NullStatus_IsFailed()
    {
        Assert.Equal(PipelineStepStatus.Failed, CoreRunStepStatusMapper.From((string?)null));
    }
}
