using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pure decision library for the completion-loop re-trigger (loop id
/// <c>completion.retrigger-transient-abort-per-job</c> in
/// <c>docs/loop-inventory.md</c>).
///
/// <para>
/// <b>Why this exists.</b> A watchdog/transient process abort is a
/// runner/OS outcome, not an agent decision: the CLI was killed mid-run,
/// so the task never got to finish. <see cref="RunOutcomePolicy"/> maps
/// such an abort to <see cref="OutcomeActionKind.NotifyUserAndStop"/> with
/// <see cref="RunIssueKind.WatchdogTimeout"/>, which dead-ends the task in
/// 5-human-review (ASS-665). The completion loop instead re-spawns the same
/// job a bounded number of times so a transient kill self-heals instead of
/// requiring an operator.
/// </para>
///
/// <para>
/// <b>Scope.</b> Only <see cref="RunIssueKind.WatchdogTimeout"/> is
/// transient and therefore re-triggerable. <see cref="RunIssueKind.EnvironmentBlocker"/>
/// is unrecoverable by the agent and <see cref="RunIssueKind.PermissionBlocked"/>
/// needs a human, so both still fall through to human review. The decider is
/// pure (ADR-0032): it only answers "may this abort be retried?"; the runner
/// owns the counter, the re-spawn, and the terminal escalation.
/// </para>
///
/// <para>
/// <b>Boundedness.</b> The runner passes the remaining budget; once it
/// reaches zero the decider returns <c>false</c> and the run escalates to
/// human review. The terminal state is always escalation, so the loop
/// cannot run unbounded - the breaker proven by
/// <c>CompletionRetriggerBreakerTest</c>.
/// </para>
/// </summary>
public static class CompletionRetriggerDecider
{
    /// <summary>
    /// Maximum number of automatic re-triggers per job for a transient
    /// abort before the loop gives up and escalates to human review.
    /// Counted per job and reset when the job leaves the run loop. Documented
    /// in <c>docs/loop-inventory.md</c> (completion.retrigger-transient-abort-per-job);
    /// change both in the same commit when tuning.
    /// </summary>
    public const int DefaultBudget = 2;

    /// <summary>
    /// True when a finished run's issue is a transient process abort that may
    /// be automatically re-triggered, given the remaining budget. False for
    /// non-transient issues or an exhausted budget - both route to human
    /// review.
    /// </summary>
    public static bool ShouldRetrigger(RunIssueKind issueKind, int budgetRemaining)
        => budgetRemaining > 0 && IsTransientAbort(issueKind);

    /// <summary>
    /// Whether an issue kind represents a transient process abort (a runner
    /// outcome, not an agent decision) that is safe to retry.
    /// </summary>
    public static bool IsTransientAbort(RunIssueKind issueKind)
        => issueKind == RunIssueKind.WatchdogTimeout;
}
