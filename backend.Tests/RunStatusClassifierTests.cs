
using Xunit;

namespace AgentStudio.Tests;

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(137)]
    [InlineData(null)]
    public void SentinelDetected_AnyExitCode_IsCompleted(int? exitCode)
    {
        // claude-code in stream-json mode lingers after the result frame; we
        // kill the OS process so OnCliFinished can run, but the agent's job
        // succeeded. The kill produces exitCode = -1 on Windows, but the
        // status must reflect the agent's outcome (completed), not the kill.
        Assert.Equal(
            RunStatuses.Completed,
            RunStatusClassifier.Classify(exitCode, RunStopReason.SentinelDetected));
    }

    [Theory]
    [InlineData(RunStopReason.UserStop)]
    [InlineData(RunStopReason.FollowupPause)]
    [InlineData(RunStopReason.Watchdog)]
    [InlineData(RunStopReason.Cancelled)]
    public void DeliberateStop_IsStopped_NeverTheFailedPreconditionForClassifierUnknown(RunStopReason reason)
    {
        // ClassifierUnknown is only reachable when the run status is "failed"
        // (AgentOutcomeAnalyzer.ResolveIssueKind returns ClassifierUnknown only
        // on a failed run with real agent text). A deliberately stopped run -
        // user pause, Pause & Send, watchdog kill, or cancellation - must
        // resolve to "stopped", never "failed", so it can never be mistaken for
        // the classifier-unknown path even though Windows reports exitCode = -1.
        var status = RunStatusClassifier.Classify(-1, reason);
        Assert.Equal(RunStatuses.Stopped, status);
        Assert.NotEqual(RunStatuses.Failed, status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(137)]
    [InlineData(null)]
    public void SilentCompletion_AnyExitCode_IsCompleted(int? exitCode)
    {
        // Codex stopped after a successful tool call without a closing
        // sentinel. The runner killed the lingering process via
        // RunStopReason.SilentCompletion so the post-run pipeline can run;
        // status must be Completed so the normal "move to 4-auto-review"
        // path applies instead of "stopped → stays in 3-progress".
        Assert.Equal(
            RunStatuses.Completed,
            RunStatusClassifier.Classify(exitCode, RunStopReason.SilentCompletion));
    }

    [Fact]
    public void AgentGitViolation_OverridesTaskDoneSentinel()
    {
        var outcome = new AgentOutcome(
            AgentOutcomeKind.Done,
            Summary: "[agent-git-violation] Worker CLI advanced git HEAD.",
            MatchedSentinel: true,
            SentinelKeyword: "DONE",
            Reason: "worker agent changed git history during its run",
            AgentTextChars: 40,
            OutputLineCount: 2,
            DurationSeconds: 30)
        {
            IssueKind = RunIssueKind.AgentGitViolation
        };

        var terminal = TerminalRunOutcomeClassifier.Classify(RunStatuses.Completed, outcome, commitsDuringRun: 1);

        Assert.Equal(TerminalRunOutcomeKinds.Failed, terminal.Kind);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }
}
