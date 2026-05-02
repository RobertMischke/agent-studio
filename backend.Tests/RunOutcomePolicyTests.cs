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
    /// THE regression: session-lost recovery ran with a user follow-up,
    /// agent exited 4.6 s with no output. Policy must re-issue with stronger
    /// framing and post a meta message into the chat.
    /// </summary>
    [Fact]
    public void Recovery_NoOpWithFollowup_Reissues()
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
        Assert.Contains("Re-issuing", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Done" after 3 seconds with a 5-char reply is structurally the same
    /// failure shape as a NoOp - the agent didn't perform the work. Policy
    /// must still re-issue.
    /// </summary>
    [Fact]
    public void Recovery_FastFakeDoneWithFollowup_Reissues()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 3.0, agentChars: 5),
            followupPrompt: "redo it",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.True(action.IsHeuristicFallback);
    }

    /// <summary>
    /// One auto-reissue, then the orchestrator stops and tells the user.
    /// Without this cap, a deterministic loop could burn quota.
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
        Assert.Contains("retried", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
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
