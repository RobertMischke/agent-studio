using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// What the orchestrator should do after a CLI run finishes. Pure data,
/// applied by <see cref="ProjectRunner"/>.
/// </summary>
public enum OutcomeActionKind
{
    /// <summary>Honor the run's exit status. Move the job per <see cref="RunCompletionPolicy"/>.</summary>
    Accept,
    /// <summary>Accept the run but post a meta message into the chat for the user.</summary>
    NotifyUserAndAccept,
    /// <summary>Re-issue the same intent with a stronger framing because the agent did not honor the user request.</summary>
    ReissueWithStrongerFraming,
    /// <summary>Halt the job and post a meta message explaining why a re-issue did not run.</summary>
    NotifyUserAndStop
}

/// <summary>
/// Pure description of the orchestrator's decision after a run. Side effects
/// (writing into the chat log, kicking off another run) are applied by the
/// runner, never inside the policy.
/// </summary>
public sealed record OutcomeAction(
    OutcomeActionKind Kind,
    string MetaMessage,
    bool IsHeuristicFallback,
    string? FollowupRetryPrompt = null,
    int RetryAttempt = 0);

/// <summary>
/// Pure decision library that maps (intent, run plan, outcome, follow-up,
/// retry count) to an <see cref="OutcomeAction"/>.
///
/// <para>
/// <b>Design center.</b> The product was previously prompt-driven: we asked
/// the agent to "treat this as a continuation" and accepted whatever came
/// back, even when the run was a 4-second no-op that ignored the user's
/// follow-up. This policy makes the orchestrator a deterministic arbiter:
/// when the agent's report contradicts the structural evidence (no edits,
/// near-zero duration) AND the user supplied a follow-up that has not been
/// honored, the orchestrator re-issues the work itself with stronger
/// framing rather than letting the inconsistency stand. When no hard
/// sentinel matched and the verdict came from the heuristic, the user is
/// always told (the meta channel makes the heuristic visible).
/// </para>
/// </summary>
public static class RunOutcomePolicy
{
    /// <summary>Maximum number of automatic re-issue attempts before the orchestrator gives up and asks the user.</summary>
    public const int MaxAutoReissueAttempts = 1;

    /// <summary>
    /// Decide what to do with a finished run.
    /// </summary>
    /// <param name="intent">Why the run was started.</param>
    /// <param name="plan">The plan the runner executed.</param>
    /// <param name="outcome">The analyzed agent outcome from <see cref="AgentOutcomeAnalyzer"/>.</param>
    /// <param name="followupPrompt">The follow-up the user typed (only meaningful for <see cref="RunIntent.UserContinue"/>).</param>
    /// <param name="reissueAttempt">How many times this same intent has already been re-issued by the orchestrator. Used to cap retries.</param>
    public static OutcomeAction Decide(
        RunIntent intent,
        RunPlan plan,
        AgentOutcome outcome,
        string? followupPrompt,
        int reissueAttempt)
    {
        var heuristic = !outcome.MatchedSentinel;
        var hasFollowup = !string.IsNullOrWhiteSpace(followupPrompt);
        var isRecovery = string.Equals(plan.EventKind, "recovery", StringComparison.OrdinalIgnoreCase);
        var isResumeContinue = intent == RunIntent.UserContinue
            && string.Equals(plan.EventKind, "continue", StringComparison.OrdinalIgnoreCase)
            && plan.ResumeFlag;

        // No-effort completion: agent exited fast with little or no text. We
        // only treat this as a re-issue trigger on a real Resume-continue. On
        // a Recovery run, a fast exit usually means the agent never had
        // session context (Claude CLI capture failed once and we are now
        // re-recovering from disk); auto-re-issuing in that case stacks
        // another recovery on top of the same broken state and burns quota.
        var lookedLikeNoEffortDone =
            outcome.Kind == AgentOutcomeKind.Done
            && outcome.DurationSeconds < AgentOutcomeAnalyzer.NoOpDurationThresholdSeconds
            && outcome.AgentTextChars < 50;

        var triggersReissue =
            isResumeContinue
            && hasFollowup
            && (outcome.Kind == AgentOutcomeKind.NoOp || lookedLikeNoEffortDone);

        if (triggersReissue && reissueAttempt < MaxAutoReissueAttempts)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                MetaMessage: "The agent exited without acting on your follow-up. Re-issuing your request with a sharper framing.",
                IsHeuristicFallback: heuristic,
                FollowupRetryPrompt: followupPrompt,
                RetryAttempt: reissueAttempt + 1);
        }
        if (triggersReissue && reissueAttempt >= MaxAutoReissueAttempts)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "I retried your follow-up once and the agent still exited without acting on it. Please rephrase or check the agent CLI.",
                IsHeuristicFallback: heuristic);
        }

        // Recovery + follow-up + fast no-output: do NOT auto-re-issue. Tell
        // the user what happened so they can re-send or rephrase. The
        // follow-up is preserved in cli-output.log either way.
        if (isRecovery && hasFollowup && (outcome.Kind == AgentOutcomeKind.NoOp || lookedLikeNoEffortDone))
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "Recovery from session loss did not produce useful output. Your follow-up is preserved in the log; you can re-send it or rephrase.",
                IsHeuristicFallback: heuristic);
        }

        // Heuristic verdicts always surface as a meta message so the user can
        // see when the deterministic contract did not match. This is the
        // "fallback warning" the philosophy section in ROADMAP describes.
        if (heuristic && outcome.Kind != AgentOutcomeKind.Unknown)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: $"No structured signal from the agent. Heuristic verdict: {outcome.Kind.ToString().ToLowerInvariant()}.",
                IsHeuristicFallback: true);
        }

        if (outcome.Kind == AgentOutcomeKind.Unknown)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "Could not classify the agent's reply. Please review the activity log.",
                IsHeuristicFallback: true);
        }

        return new OutcomeAction(
            Kind: OutcomeActionKind.Accept,
            MetaMessage: string.Empty,
            IsHeuristicFallback: false);
    }

    /// <summary>
    /// Build the prompt used when the orchestrator re-issues a follow-up that
    /// the agent failed to honor. Kept here (next to the policy that decides
    /// to re-issue) so any change to the policy and its prompt move together.
    /// </summary>
    public static string BuildReissueFollowupPrompt(string originalFollowup) =>
        "The previous run exited without acting on the user request below. "
      + "Treat the user request as the only thing that matters now. "
      + "Do not reply 'task done' unless you have actually performed the work the user asked for. "
      + "If you cannot perform the request, emit [[TASK_BLOCKED:<reason>]] and explain why.\n\n"
      + "User request:\n"
      + originalFollowup.Trim();
}
