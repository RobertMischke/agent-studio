using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the post-run policy that turns a run's outcome into the
/// orchestrator's next move. Tests follow the same matrix style as
/// <see cref="TaskRunnerPlanTests"/>: the inputs are intent + plan event
/// kind + outcome shape + retry attempt; the outputs are the
/// <see cref="OutcomeAction"/> kind, meta-message, and retry budget.
///
/// The bug class this guards: pre-policy, the orchestrator passively
/// accepted whatever the agent reported. A 4-second exit after a recovery
/// continue with a user follow-up was indistinguishable from a real
/// completion. Tests below pin the deterministic re-issue and the
/// heuristic-warning paths so a future refactor can't quietly drop them.
/// </summary>
public class RunOutcomePolicyTests
{
    private static RunPlan RecoveryPlan(string state = TaskStates.Progress) =>
        new(
            PromptTemplate: RuntimePromptService.RunnerRecoveryContinuation,
            PromptVariables: new Dictionary<string, string?> { ["user_followup"] = "do the work please" },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "recovery",
            EventReason: "no session recorded",
            EventInputSessionId: null,
            MoveJobToProgress: state != TaskStates.Progress,
            MarkSessionChainRecovery: true,
            WriteCutMarker: true,
            CutMarkerReason: "session lost",
            PersistSessionName: null,
            ClearStaleSessionName: false);

    private static RunPlan ContinuePlan() =>
        new(
            PromptTemplate: null,
            PromptVariables: new Dictionary<string, string?>(),
            PromptOverride: "follow-up",
            SessionToResume: "abc",
            ResumeFlag: true,
            EventKind: "continue",
            EventReason: null,
            EventInputSessionId: "abc",
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

    private static AgentOutcome Outcome(
        AgentOutcomeKind kind,
        bool sentinel = false,
        double duration = 30.0,
        int agentChars = 200) =>
        new(kind, "summary", sentinel, sentinel ? "DONE" : null, sentinel ? null : "heuristic", agentChars, agentChars / 5, duration);

    /// <summary>
    /// Graceful Recovery (ADR-0006): even when the prior session is
    /// unrecoverable, Recovery + follow-up + no-output gets ONE re-issue
    /// with a sharper, recovery-aware framing. The user explicitly asked
    /// for "selbst wenn die Session nicht fortgesetzt werden kann, soll
    /// es weitergehen", so dropping the follow-up is not acceptable.
    /// </summary>
    [Fact]
    public void Recovery_NoOpWithFollowup_GracefullyReissuesOnce()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "Please process the task again",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.Equal("Please process the task again", action.FollowupRetryPrompt);
        Assert.Contains("Recovery from session loss", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One auto-re-issue, then stop. The retry budget is shared across
    /// Resume-continue and Recovery so a stuck loop cannot burn quota.
    /// </summary>
    [Fact]
    public void Recovery_NoOpAfterReissue_GivesUp()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "do it",
            reissueAttempt: RunOutcomePolicy.MaxAutoReissueAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
    }

    /// <summary>
    /// The recovery-aware re-issue prompt must explicitly tell the agent
    /// the session is unrecoverable and that the user request is the only
    /// context. Without this, the agent's "I'll wait for your next request"
    /// reply (the symptom that triggered the redesign) repeats.
    /// </summary>
    [Fact]
    public void RecoveryAwarePrompt_TellsAgentHistoryIsGone()
    {
        var prompt = RunOutcomePolicy.BuildReissueFollowupPrompt("look at the screenshot and improve the layout", recoveryContext: true);
        Assert.Contains("unrecoverable", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standing by", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("look at the screenshot and improve the layout", prompt);
        Assert.Contains("[[TASK_BLOCKED", prompt);
    }

    /// <summary>
    /// Resume-continue with follow-up + fast no-output IS the case where
    /// auto-reissue still fires: the session was alive, the agent had real
    /// context, and it still ignored the follow-up. One retry with sharper
    /// framing, then stop.
    /// </summary>
    [Fact]
    public void ResumeContinue_NoOpWithFollowup_Reissues()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "Please tighten the spacing",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.Equal("Please tighten the spacing", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Same shape as above but we already re-issued once: orchestrator stops
    /// and asks the user to step in.
    /// </summary>
    [Fact]
    public void ResumeContinue_NoOpAfterReissue_GivesUp()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "do it",
            reissueAttempt: RunOutcomePolicy.MaxAutoReissueAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
    }

    /// <summary>
    /// Sentinel-backed Done with a real run duration is accepted silently.
    /// This is the "happy path" the meta channel must stay quiet on.
    /// </summary>
    [Fact]
    public void RealRun_SentinelDone_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: true, duration: 90.0, agentChars: 1500),
            followupPrompt: "tighten the code",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    /// <summary>
    /// No sentinel, heuristic concluded "done" - we accept but the meta
    /// channel must surface the fact that the deterministic contract did
    /// not match. This is the "show the user when we are guessing" promise.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicDone_AcceptedWithWarning()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.ManualStart,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 60.0, agentChars: 1500),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.True(action.IsHeuristicFallback);
        Assert.Contains("heuristic", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continue without follow-up + agent says NoOp: nothing for the policy
    /// to do (no follow-up to re-issue). Accept with a heuristic warning.
    /// </summary>
    [Fact]
    public void Continue_NoFollowup_NoOp_AcceptsWithWarning()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.NotEqual(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
    }

    /// <summary>
    /// The agent's question is already visible in the chat; an extra
    /// "[heuristic] verdict: needsinput" line is just noise. Policy should
    /// stay silent on NeedsInput so the user can respond directly to the
    /// agent or use the quick-reply chips.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicNeedsInput_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NeedsInput, sentinel: false, duration: 30.0, agentChars: 200),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    /// <summary>
    /// Codex silent-completion: the runtime detector tripped, the analyzer
    /// surfaced <see cref="RunIssueKind.SilentCompletion"/>. Policy must
    /// route through <see cref="OutcomeActionKind.NotifyUserAndAccept"/> so
    /// the regular post-completion path runs (auto-review move + aspect
    /// calls) while the chat surface clearly distinguishes the case from a
    /// real sentinel-backed Done.
    /// </summary>
    [Fact]
    public void SilentCompletion_NotifiesUserAndAccepts_RoutesThroughAutoReview()
    {
        var outcome = Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 320.0, agentChars: 1500)
            with { IssueKind = RunIssueKind.SilentCompletion, Summary = "Codex stopped after final tool call without a closing sentinel (silence=92s)." };
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            outcome,
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.SilentCompletion, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SilentCompletion, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Contains("sentinel", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Progress mid-run shape: the agent kept moving, no need for a meta
    /// message. The activity log itself shows the run is alive.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicProgress_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.ManualStart,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Progress, sentinel: false, duration: 45.0, agentChars: 800),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
    }

    /// <summary>
    /// Run produced *some* agent text but the deterministic classifier could
    /// not map it to a known outcome. This used to be the generic
    /// heuristicfallback bucket; it now surfaces as the concrete
    /// classifier-unknown category so the project-level observability
    /// counters can distinguish it from permission and watchdog issues.
    /// </summary>
    [Fact]
    public void RealRun_UnknownWithText_AcceptedWithWarning()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 30.0, agentChars: 250),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(RunIssueKind.ClassifierUnknown, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.ClassifierUnknown, action.MessageKind);
        Assert.Contains("classify", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run produced no agent text (e.g. claude rejected the resume target
    /// and exited with error_during_execution). The user already sees the
    /// system-error block in the chat and a [capture-fail] decision
    /// message right next to it - the frontend's protocol-pane banner also
    /// surfaces the failed-run state explicitly. Adding "[heuristic] Could
    /// not classify the agent's reply." on top is just noise. Policy
    /// stays silent so the meta channel does not pile redundant warnings
    /// on a single failed turn.
    /// </summary>
    [Fact]
    public void FailedRun_UnknownWithoutText_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 2.0, agentChars: 0),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    [Fact]
    public void EnvironmentBlocker_RoutesStraightToHumanReview_NoSoftIntervention()
    {
        // EnvironmentBlocker is unrecoverable by the agent: the OS / sandbox
        // refused the work. Policy must NotifyUserAndStop on the first
        // occurrence and tag the message kind so the chat log + the
        // ProjectRunner human-review router pick it up.
        var diagnosis = "Codex Windows sandbox refused to execute commands (cli=codex): set sandbox_mode = \"danger-full-access\".";
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 3.2, agentChars: 0) with
            {
                IssueKind = RunIssueKind.EnvironmentBlocker,
                Summary = diagnosis
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.EnvironmentBlocker, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.EnvironmentBlocker, action.MessageKind);
        Assert.Equal(diagnosis, action.MetaMessage);
        Assert.Null(action.FollowupRetryPrompt);
    }

    [Fact]
    public void PermissionBlocked_FirstOccurrence_ReissuesOneSoftIntervention()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 65.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.PermissionBlocked,
                Summary = "Tool permission failure prevented the agent from inspecting the workspace."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(RunIssueKind.PermissionBlocked, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SoftIntervention, action.MessageKind);
        Assert.Contains("available permissions", action.FollowupRetryPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermissionBlocked_AfterOneIntervention_StopsWithConcreteCategory()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 65.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.PermissionBlocked,
                Summary = "Tool permission failure prevented the agent from inspecting the workspace."
            },
            followupPrompt: null,
            reissueAttempt: RunOutcomePolicy.MaxSoftInterventionAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(RunIssueKind.PermissionBlocked, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.PermissionBlocked, action.MessageKind);
        Assert.Contains("permission", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTerminalSentinel_FirstOccurrence_AsksForSentinelOnce()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 35.0, agentChars: 900) with
            {
                IssueKind = RunIssueKind.MissingTerminalSentinel,
                Summary = "Agent text suggests the task is done (heuristic)."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, action.IssueKind);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
    }

    [Fact]
    public void WatchdogTimeout_IsConcreteGiveupNotHeuristicFallback()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 60.0, agentChars: 0) with
            {
                IssueKind = RunIssueKind.WatchdogTimeout,
                Summary = "Run was killed by the watchdog after silence."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(OrchestratorMessageKind.WatchdogTimeout, action.MessageKind);
    }
}
