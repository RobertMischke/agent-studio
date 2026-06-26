

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture-level lock for the loop-inventory entry
/// <c>completion.retrigger-transient-abort-per-job</c> (the completion-loop
/// re-trigger that restarts a watchdog/transient-killed run instead of
/// dead-ending it in human review):
///
/// <list type="number">
///   <item>The decider and its budget constant live in code
///   (<see cref="CompletionRetriggerDecider"/>).</item>
///   <item>The breaker behaviour is pinned: an exhausted budget stops
///   re-triggering (the runner then escalates to human review), and only a
///   transient watchdog abort is ever re-triggered - environment/permission
///   issues never are.</item>
///   <item>The inventory entry references this exact test file path.</item>
/// </list>
///
/// <para>
/// ADR-0032 rule: every loop class is registered with a breaker. The
/// completion loop keeps a task alive by re-spawning after a transient
/// process abort; this per-job budget is the breaker that stops a
/// persistently-aborting run from spinning forever.
/// </para>
/// </summary>
public class CompletionRetriggerBreakerTest
{
    [Fact]
    public void DeciderType_Exists()
    {
        Assert.NotNull(typeof(CompletionRetriggerDecider));
    }

    [Fact]
    public void DefaultBudget_HasExpectedValue()
    {
        // Pin the default documented in docs/contracts/loop-inventory.md
        // (completion.retrigger-transient-abort-per-job). Change both in the
        // same commit when tuning the budget.
        Assert.Equal(2, CompletionRetriggerDecider.DefaultBudget);
    }

    [Fact]
    public void BudgetExhausted_StopsRetriggering()
    {
        // With the budget spent, a transient abort may NOT keep the loop alive
        // - the runner falls through to human-review escalation.
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budgetRemaining: 0));
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budgetRemaining: -1));
    }

    [Fact]
    public void NonTransientAbort_NeverRetriggers()
    {
        // Unrecoverable (environment) and human-needed (permission) aborts are
        // never re-triggered, even with full budget; they go straight to a
        // human.
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.EnvironmentBlocker, budgetRemaining: 5));
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.PermissionBlocked, budgetRemaining: 5));
    }

    [Fact]
    public void BudgetMonotonicallyConvergesToEscalation()
    {
        // Walk the budget down from the default to zero: each unit allows one
        // re-trigger, and the terminal state is always "do not re-trigger"
        // (the runner escalates). This is the structural proof the loop cannot
        // run unbounded.
        for (var budget = CompletionRetriggerDecider.DefaultBudget; budget > 0; budget--)
            Assert.True(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, budget));
        Assert.False(CompletionRetriggerDecider.ShouldRetrigger(RunIssueKind.WatchdogTimeout, 0));
    }
}
