

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure decision library behind the per-task anti-endless-reissue
/// circuit breaker. The bug it guards: a soft, non-routed failure
/// (classifier-unknown / missing-terminal-sentinel / context-overflow before
/// it was typed) was re-issued without limit, producing 200+ CLI starts on a
/// single task. These tests pin (a) which issue kinds count toward the streak
/// and (b) the trip threshold, so a refactor can't silently widen or disable
/// the breaker.
/// </summary>
public class RunQuarantineBreakerTests
{
    [Theory]
    [InlineData(RunIssueKind.InfraCrash)]
    [InlineData(RunIssueKind.OrchestratorInconclusive)]
    [InlineData(RunIssueKind.MissingTerminalSentinel)]
    [InlineData(RunIssueKind.NoAgentOutput)]
    [InlineData(RunIssueKind.HeuristicDone)]
    [InlineData(RunIssueKind.None)]
    public void SoftNonRoutedFailures_CountTowardStreak(RunIssueKind kind)
    {
        Assert.True(RunQuarantineBreaker.CountsAsNoProgressFailure(kind));
    }

    [Theory]
    [InlineData(RunIssueKind.PermissionBlocked)]
    [InlineData(RunIssueKind.WatchdogTimeout)]
    [InlineData(RunIssueKind.EnvironmentBlocker)]
    [InlineData(RunIssueKind.EmptyFastExit)]
    [InlineData(RunIssueKind.ContextOverflow)]
    [InlineData(RunIssueKind.CliLaunchFailed)]
    // A transient host file lock / network glitch has its own bounded
    // retry-with-backoff; it must not accrue toward the quarantine streak
    // (AGT-1944).
    [InlineData(RunIssueKind.EnvironmentalTransient)]
    // A failed OAuth-session refresh is a shared credential/infra fault the task
    // cannot fix by re-running; it routes to human review on its own and must
    // not accrue toward the quarantine streak (AGT-2066).
    [InlineData(RunIssueKind.AuthRefreshFailed)]
    [InlineData(RunIssueKind.SilentCompletion)]
    [InlineData(RunIssueKind.Quarantined)]
    public void RoutedOrTerminalKinds_DoNotCountTowardStreak(RunIssueKind kind)
    {
        // These either route to human review on their own, have a dedicated
        // recovery breaker, are accepted as done, or are the breaker's own
        // verdict - counting them would double-trip or fight another path.
        Assert.False(RunQuarantineBreaker.CountsAsNoProgressFailure(kind));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void Trips_AtOrAboveThreshold(int consecutiveFails, bool expectedTrip)
    {
        Assert.Equal(expectedTrip, RunQuarantineBreaker.ShouldQuarantine(consecutiveFails));
    }

    [Fact]
    public void DefaultThreshold_IsThree()
    {
        Assert.Equal(3, RunQuarantineBreaker.DefaultFailThreshold);
    }
}
