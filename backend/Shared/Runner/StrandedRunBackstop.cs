namespace AgentStudio.Shared;

/// <summary>
/// Drive-to-conclusion backstop decision (pure). A run can finish the CLI loop
/// without any typed route claiming it: the outcome maps to <c>Accept</c>
/// (a failed run with no agent text → <c>NoAgentOutput</c>) or to
/// <c>NotifyUserAndAccept</c> (a CLI launch/resume failure → <c>CliLaunchFailed</c>),
/// neither of which moves the task to review nor escalates it. The runner's
/// fall-through then logs "leaving it in progress for review or recovery" and
/// the task sits in <c>3-progress</c> forever, because pickup only scans
/// <c>2-ready</c> — a permanent zombie.
///
/// <para>This is the recurring "in-progress lane kaputt" incident: a rapid
/// stale-session resume crash (exit code 1, zero output) that the typed
/// human-review routes never matched. See
/// <c>docs/concepts/runner-stability-incidents.html</c>.</para>
///
/// <para>Invariant: <b>no genuinely FAILED run ever stays in 3-progress</b>;
/// it always reaches a terminal lane with an honest, visible reason. The only
/// runs that legitimately stay in progress at the fall-through are a deliberate
/// stop (<see cref="RunStatuses.Stopped"/>, the operator/user stopped it and
/// will resume) and a run still awaiting user input
/// (<see cref="AgentOutcomeKind.NeedsInput"/>, the question is visible in the
/// chat for the user to answer). Everything else that failed must escalate.</para>
/// </summary>
public static class StrandedRunBackstop
{
    /// <summary>
    /// True when a run that fell through to "leaving it in progress" must
    /// instead be escalated to human review so it cannot strand in 3-progress.
    /// </summary>
    /// <param name="executionStatus">The terminal run status (see <see cref="RunStatuses"/>).</param>
    /// <param name="outcomeKind">The classified agent outcome for the run.</param>
    public static bool MustEscalateStrandedRun(string? executionStatus, AgentOutcomeKind outcomeKind)
        => string.Equals(executionStatus, RunStatuses.Failed, System.StringComparison.OrdinalIgnoreCase)
           && outcomeKind != AgentOutcomeKind.NeedsInput;
}
