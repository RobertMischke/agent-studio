using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the (exitCode, stopReason) -> status truth table.
///
/// The bug this guards against: <c>Process.Kill(entireProcessTree: true)</c>
/// returns exitCode = -1 on Windows. Without a reason hint the legacy
/// classifier <c>exitCode == 0 ? "completed" : "failed"</c> turned every
/// user pause, every Pause &amp; Send, and every watchdog kill into a
/// "Task execution failed with exit code -1" error modal in the UI.
/// </summary>
public class RunStatusClassifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(137)]
    [InlineData(null)]
    public void AnyExitCode_WithUserStop_IsStopped(int? exitCode)
    {
        Assert.Equal(RunStatuses.Stopped, RunStatusClassifier.Classify(exitCode, RunStopReason.UserStop));
    }

    [Theory]
    [InlineData(RunStopReason.UserStop)]
    [InlineData(RunStopReason.FollowupPause)]
    [InlineData(RunStopReason.Watchdog)]
    [InlineData(RunStopReason.Cancelled)]
    public void AnyDeliberateReason_OverridesExitCode(RunStopReason reason)
    {
        // The headline regression: Windows Process.Kill yields -1.
        Assert.Equal(RunStatuses.Stopped, RunStatusClassifier.Classify(-1, reason));
    }

    [Fact]
    public void NaturalExitZero_WithoutReason_IsCompleted()
    {
        Assert.Equal(RunStatuses.Completed, RunStatusClassifier.Classify(0, RunStopReason.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(137)]
    [InlineData(null)]
    public void NaturalExitNonZero_WithoutReason_IsFailed(int? exitCode)
    {
        Assert.Equal(RunStatuses.Failed, RunStatusClassifier.Classify(exitCode, RunStopReason.None));
    }
}
