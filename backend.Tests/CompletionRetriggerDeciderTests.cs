using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the pure completion-loop re-trigger decision (loop id
/// completion.retrigger-transient-abort-per-job). Only a transient watchdog
/// abort is re-triggerable, and only while budget remains; everything else
/// routes to human review.
/// </summary>
public class CompletionRetriggerDeciderTests
{
    [Fact]
    public void WatchdogTimeout_WithBudget_Retriggers()
    {
        Assert.True(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budgetRemaining: 2));
        Assert.True(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budgetRemaining: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WatchdogTimeout_NoBudget_DoesNotRetrigger(int budget)
    {
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budget));
    }

    [Theory]
    [InlineData(RunIssueKind.EnvironmentBlocker)]   // unrecoverable -> human
    [InlineData(RunIssueKind.PermissionBlocked)]    // needs a human
    [InlineData(RunIssueKind.MissingTerminalSentinel)]
    [InlineData(RunIssueKind.HeuristicDone)]
    [InlineData(RunIssueKind.None)]
    public void NonTransientIssues_NeverRetrigger_EvenWithBudget(RunIssueKind issue)
    {
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(issue, budgetRemaining: 5));
        Assert.False(CompletionRetriggerDecider.IsTransientAbort(issue));
    }

    [Fact]
    public void OnlyWatchdogTimeoutIsTransient()
    {
        Assert.True(CompletionRetriggerDecider.IsTransientAbort(RunIssueKind.WatchdogTimeout));
    }
}
