using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the drive-to-conclusion invariant: a genuinely FAILED run that falls
/// through the runner's typed routes must be escalated to human review, never
/// left in 3-progress (where pickup never revisits it -> a permanent zombie).
/// Regression guard for the recurring "in-progress lane kaputt" incident
/// (rapid stale-session resume crash, exit=1, 0 output -> NoAgentOutput ->
/// Accept -> stranded). See docs/wiki/concepts/runner-stability-incidents.html.
/// </summary>
public class StrandedRunBackstopTests
{
    [Theory]
    // A failed run that produced nothing parseable (NoAgentOutput / Unknown):
    // the exact shape that used to strand. Must escalate.
    [InlineData("failed", AgentOutcomeKind.Unknown, true)]
    // Any other failed-and-fell-through shape must also escalate, regardless of
    // the heuristic the run text happened to land on.
    [InlineData("failed", AgentOutcomeKind.Done, true)]
    [InlineData("failed", AgentOutcomeKind.Blocked, true)]
    [InlineData("failed", AgentOutcomeKind.Progress, true)]
    [InlineData("failed", AgentOutcomeKind.NoOp, true)]
    // Case-insensitive on the status string.
    [InlineData("Failed", AgentOutcomeKind.Unknown, true)]
    // A failed run still awaiting user input legitimately stays in progress:
    // the question is visible in the chat for the user to answer.
    [InlineData("failed", AgentOutcomeKind.NeedsInput, false)]
    // A deliberate stop (status=stopped) is an interruption, not a failure:
    // the operator/user will resume it. Leave in progress.
    [InlineData("stopped", AgentOutcomeKind.Unknown, false)]
    [InlineData("stopped", AgentOutcomeKind.Done, false)]
    // A successful run is moved to review by the happy path, not here.
    [InlineData("completed", AgentOutcomeKind.Done, false)]
    [InlineData("completed", AgentOutcomeKind.Unknown, false)]
    // No status at all is not a positive failure signal.
    [InlineData(null, AgentOutcomeKind.Unknown, false)]
    public void MustEscalateStrandedRun_OnlyFiresOnGenuineFailures(
        string? executionStatus, AgentOutcomeKind outcomeKind, bool expected)
    {
        Assert.Equal(expected, StrandedRunBackstop.MustEscalateStrandedRun(executionStatus, outcomeKind));
    }

    [Fact]
    public void FailedRunWithNoOutput_IsNeverLeftInProgress()
    {
        // The specific regression: exit=1, zero agent text -> NoAgentOutput ->
        // outcome.Kind == Unknown -> action Accept. Before the backstop this
        // returned to "leaving it in progress" and stranded in 3-progress.
        Assert.True(StrandedRunBackstop.MustEscalateStrandedRun("failed", AgentOutcomeKind.Unknown));
    }
}
