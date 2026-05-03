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

        // No-effort completion: agent exited fast with little or no text.
        // Graceful Recovery (ADR-0006 supersedes ADR-0003): even when the
        // prior session is unrecoverable, we re-issue ONCE with a sharper
        // framing rather than letting the user's follow-up fall on the
        // floor. The retry prompt makes the follow-up the only thing that
        // matters and explicitly tells the agent that conversation history
        // is gone, so "I'll wait for your next request" is not an
        // acceptable answer. The retry budget is the same as the
        // Resume-Continue path - one shot, then NotifyUserAndStop.
        var lookedLikeNoEffortDone =
            outcome.Kind == AgentOutcomeKind.Done
            && outcome.DurationSeconds < AgentOutcomeAnalyzer.NoOpDurationThresholdSeconds
            && outcome.AgentTextChars < 50;

        var noEffortShape = outcome.Kind == AgentOutcomeKind.NoOp || lookedLikeNoEffortDone;
        var triggersReissue =
            (isResumeContinue || isRecovery)
            && intent == RunIntent.UserContinue
            && hasFollowup
            && noEffortShape;

        if (triggersReissue && reissueAttempt < MaxAutoReissueAttempts)
        {
            var msg = isRecovery
                ? "Recovery from session loss did not produce useful output. Re-issuing your follow-up with a sharper framing so the agent acts on it even without prior conversation context."
                : "The agent exited without acting on your follow-up. Re-issuing your request with a sharper framing.";
            return new OutcomeAction(
                Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                MetaMessage: msg,
                IsHeuristicFallback: heuristic,
                FollowupRetryPrompt: followupPrompt,
                RetryAttempt: reissueAttempt + 1);
        }
        if (triggersReissue && reissueAttempt >= MaxAutoReissueAttempts)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "I retried your follow-up once and the agent still exited without acting on it. Please rephrase, check the agent CLI, or look at the Trace view.",
                IsHeuristicFallback: heuristic);
        }

        // Heuristic verdicts surface only when the user gets actionable
        // signal from the meta message. NeedsInput is the obvious skip:
        // the agent's question is already visible in the chat and the
        // frontend's quick-reply chips react to it directly; an extra
        // "[heuristic] verdict: needsinput" line is just noise.
        // Progress is similarly self-evident from the run still moving.
        // We keep the warning for Done (the agent might be claiming done
        // without doing the work) and Unknown (we genuinely cannot tell).
        if (heuristic && outcome.Kind == AgentOutcomeKind.Done)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "[heuristic] Agent reports done (no structured signal).",
                IsHeuristicFallback: true);
        }

        if (outcome.Kind == AgentOutcomeKind.Unknown)
        {
            // When the run produced no agent text at all, the user is
            // already seeing the cause (a CLI-side system error block,
            // and likely a [capture-fail] decision message right next to
            // it). Adding "[heuristic] Could not classify the agent's
            // reply." on top is redundant noise - there is no reply to
            // classify. The frontend's protocol-pane banner surfaces the
            // failed-run state explicitly. Skip the meta message.
            if (outcome.AgentTextChars == 0)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.Accept,
                    MetaMessage: string.Empty,
                    IsHeuristicFallback: false);
            }
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "[heuristic] Could not classify the agent's reply.",
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
    ///
    /// <para>
    /// <paramref name="recoveryContext"/> is true when the previous run was a
    /// Recovery (no resumable session). The framing then tells the agent
    /// explicitly that conversation history is unavailable and that the
    /// user request, plus any task evidence on disk (prompt.md, status.md,
    /// logs), is the entire context. This is the load-bearing prompt for
    /// graceful recovery: if it fails, the orchestrator gives up.
    /// </para>
    /// </summary>
    public static string BuildReissueFollowupPrompt(string originalFollowup, bool recoveryContext = false)
    {
        var head = recoveryContext
            ? "The previous CLI session for this task is unrecoverable. There is no conversation history to read; the only context you have is the job folder on disk (prompt.md, status.md, logs/cli-output.log) and the user request below. Do the work the user asked for, end-to-end. "
              + "Reading 'I'll wait for your request' or 'standing by' as a valid answer is not acceptable - the user already gave you the request. "
            : "The previous run exited without acting on the user request below. "
              + "Treat the user request as the only thing that matters now. ";
        return head
             + "Do not reply 'task done' unless you have actually performed the work the user asked for. "
             + "If you genuinely cannot perform the request, emit [[TASK_BLOCKED:<reason>]] and explain why.\n\n"
             + "User request:\n"
             + (originalFollowup ?? string.Empty).Trim();
    }
}
