using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the parser for the review-decision orchestrator's two grammars:
/// the agent-side <c>[[TASK_NEEDS_INPUT]]</c> sentinel that landed the
/// job in 4-review and the orchestrator-side
/// <c>[[ORCHESTRATOR_DECISION]]</c> response sentinel produced by the
/// fast-model session.
/// </summary>
public class ReviewDecisionParsingTests
{
    [Fact]
    public void FindUnresolvedNeedsInput_ReturnsLatestWhenNoFollowUp()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] working...",
            "[12:00:01.000] [stdout] [[TASK_NEEDS_INPUT: which column should be primary?]]",
            "[12:00:02.000] [stdout] (idle)");

        var state = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);

        Assert.NotNull(state);
        Assert.Equal("which column should be primary?", state!.Reason);
    }

    [Fact]
    public void FindUnresolvedNeedsInput_ReturnsNull_WhenOrchestratorAlreadyAnswered()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_NEEDS_INPUT: pick A or B]]",
            "[12:00:30.000] [orchestrator] [reissue] Decision: pick A. Reason: roadmap mandates it.");

        var state = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);

        Assert.Null(state);
    }

    [Fact]
    public void FindUnresolvedNeedsInput_ReturnsNull_WhenSupervisorAlreadyEscalated()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_NEEDS_INPUT: needs human]]",
            "[12:00:30.000] [supervisor] [escalate] Orchestrator could not decide unattended.");

        Assert.Null(ReviewDecisionParsing.FindUnresolvedNeedsInput(log));
    }

    [Fact]
    public void FindUnresolvedNeedsInput_PrefersLatestSentinel()
    {
        var log = string.Join('\n',
            "[10:00:00.000] [stdout] [[TASK_NEEDS_INPUT: first]]",
            "[10:00:05.000] [orchestrator] [reissue] answered first",
            "[10:01:00.000] [stdout] [[TASK_NEEDS_INPUT: second]]");

        var state = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);

        Assert.NotNull(state);
        Assert.Equal("second", state!.Reason);
    }

    [Fact]
    public void FindUnresolvedNoOp_ReturnsLatestWhenNoFollowUp()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] starting...",
            "[12:00:01.000] [stdout] [[TASK_NOOP]]",
            "[12:00:02.000] [stdout] (idle)");

        var state = ReviewDecisionParsing.FindUnresolvedNoOp(log);

        Assert.NotNull(state);
        Assert.Null(state!.Reason);
    }

    [Fact]
    public void FindUnresolvedNoOp_ReturnsNull_WhenOrchestratorAlreadyReissued()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_NOOP]]",
            "[12:00:30.000] [orchestrator] [reissue] Decision: reissue (NOOP recovery).");

        Assert.Null(ReviewDecisionParsing.FindUnresolvedNoOp(log));
    }

    [Fact]
    public void FindUnresolvedNoOp_ReturnsNull_WhenSupervisorAlreadyEscalated()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_NOOP]]",
            "[12:00:30.000] [supervisor] [escalate] Orchestrator could not auto-recover NOOP.");

        Assert.Null(ReviewDecisionParsing.FindUnresolvedNoOp(log));
    }

    [Fact]
    public void FindUnresolvedNoOp_CapturesReasonWhenProvided()
    {
        var log = "[12:00:00.000] [stdout] [[TASK_NOOP: nothing actionable in prompt]]";

        var state = ReviewDecisionParsing.FindUnresolvedNoOp(log);

        Assert.NotNull(state);
        Assert.Equal("nothing actionable in prompt", state!.Reason);
    }

    [Fact]
    public void FindUnresolvedBlocked_ReturnsLatestWhenNoFollowUp()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] working...",
            "[12:00:01.000] [stdout] [[TASK_BLOCKED: awaiting user decision A/B/C]]",
            "[12:00:02.000] [stdout] (idle)");

        var state = ReviewDecisionParsing.FindUnresolvedBlocked(log);

        Assert.NotNull(state);
        Assert.Equal("awaiting user decision A/B/C", state!.Reason);
    }

    [Fact]
    public void FindUnresolvedBlocked_ReturnsNull_WhenSupervisorAlreadyEscalated()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_BLOCKED: needs human]]",
            "[12:00:30.000] [supervisor] [escalate] Orchestrator escalated BLOCKED to human review.");

        Assert.Null(ReviewDecisionParsing.FindUnresolvedBlocked(log));
    }

    [Fact]
    public void FindUnresolvedBlocked_NoReason_StillReturnsHit()
    {
        var log = "[12:00:00.000] [stdout] [[TASK_BLOCKED]]";

        var state = ReviewDecisionParsing.FindUnresolvedBlocked(log);

        Assert.NotNull(state);
        Assert.Null(state!.Reason);
    }

    [Fact]
    public void FindUnresolvedDone_IgnoresRunnerActiveStateClearedMarker()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_DONE]]",
            "[12:00:01.000] [system] [taskboard] claude CLI exited: status=completed, exitCode=0",
            "[12:00:02.000] [orchestrator] [decision] Runner active state cleared: job moved out of 3-progress externally (3-progress -> 4-auto-review)");

        var state = ReviewDecisionParsing.FindUnresolvedDone(log);

        Assert.NotNull(state);
        Assert.Equal(1, state!.LineNumber);
    }

    [Fact]
    public void FindUnresolvedDone_ReturnsNull_WhenOrchestratorAlreadyReviewed()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_DONE]]",
            "[12:00:30.000] [orchestrator] [decision] Decision: accept-as-done. Aspects: requirement-fit=pass");

        Assert.Null(ReviewDecisionParsing.FindUnresolvedDone(log));
    }

    [Fact]
    public void FindUnresolvedBlocked_IgnoresRunnerActiveStateClearedMarker()
    {
        var log = string.Join('\n',
            "[12:00:00.000] [stdout] [[TASK_BLOCKED: needs scope]]",
            "[12:00:01.000] [orchestrator] [decision] Runner active state cleared: job moved out of 3-progress externally (3-progress -> 4-auto-review)");

        var state = ReviewDecisionParsing.FindUnresolvedBlocked(log);

        Assert.NotNull(state);
        Assert.Equal("needs scope", state!.Reason);
    }

    [Fact]
    public void ParseDecision_Reissue_RoundTripsActionAndReason()
    {
        var output = "After reading the roadmap...\n[[ORCHESTRATOR_DECISION: action=reissue; reason=Roadmap names option A as canonical.]]\n[[TASK_DONE]]";

        var verdict = ReviewDecisionParsing.ParseDecision(output);

        Assert.NotNull(verdict);
        Assert.Equal(OrchestratorDecisionAction.Reissue, verdict!.Action);
        Assert.Equal("Roadmap names option A as canonical.", verdict.Reason);
    }

    [Fact]
    public void ParseDecision_Escalate_Recognised()
    {
        var verdict = ReviewDecisionParsing.ParseDecision(
            "[[ORCHESTRATOR_DECISION: action=escalate; reason=Needs user credential.]]");
        Assert.Equal(OrchestratorDecisionAction.Escalate, verdict!.Action);
    }

    [Fact]
    public void ParseDecision_AcceptAsDone_Recognised()
    {
        var verdict = ReviewDecisionParsing.ParseDecision(
            "[[ORCHESTRATOR_DECISION: action=accept-as-done; reason=Work matches contract; question is courtesy check.]]");
        Assert.Equal(OrchestratorDecisionAction.AcceptAsDone, verdict!.Action);
    }

    [Fact]
    public void ParseDecision_TolerantOfFieldOrderAndCase()
    {
        var verdict = ReviewDecisionParsing.ParseDecision(
            "[[orchestrator_decision: REASON=fine; ACTION=Accept]]");
        Assert.NotNull(verdict);
        Assert.Equal(OrchestratorDecisionAction.AcceptAsDone, verdict!.Action);
    }

    [Fact]
    public void ParseDecision_ReturnsNull_OnUnknownActionOrAbsentSentinel()
    {
        Assert.Null(ReviewDecisionParsing.ParseDecision(""));
        Assert.Null(ReviewDecisionParsing.ParseDecision("no sentinel here"));
        Assert.Null(ReviewDecisionParsing.ParseDecision("[[ORCHESTRATOR_DECISION: action=panic; reason=oh no]]"));
    }
}
