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
    private static RunPlan RecoveryPlan(string state = JobStates.Progress) =>
        new(
            PromptTemplate: RuntimePromptService.RunnerRecoveryContinuation,
            PromptVariables: new Dictionary<string, string?> { ["user_followup"] = "do the work please" },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "recovery",
            EventReason: "no session recorded",
            EventInputSessionId: null,
            MoveJobToProgress: state != JobStates.Progress,
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
    /// Recovery + follow-up + fast no-output: do NOT auto-re-issue. The
    /// previous behavior re-issued and burned quota stacking another
    /// recovery on top of a broken capture. Today the policy posts a meta
    /// message asking the user to re-send instead.
    /// </summary>
    [Fact]
    public void Recovery_NoOpWithFollowup_DoesNotAutoReissue()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "Please process the task again",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Contains("Recovery from session loss", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
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
}
