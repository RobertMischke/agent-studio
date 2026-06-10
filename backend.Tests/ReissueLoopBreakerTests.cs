
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pure-policy coverage for <see cref="ReissueLoopBreaker"/> (ASS-794): the
/// deterministic loop-breaker that stops a finished task from penduluming
/// between <c>2-ready</c> and the run loop on the multi-aspect BLOCK path. Two
/// rules in precedence order - empty follow-up diff on an already-reissued clean
/// card -> accept; budget spent -> escalate; otherwise no loop-break.
/// </summary>
public class ReissueLoopBreakerTests
{
    [Fact]
    public void Evaluate_EmptyDiffOnReissuedCleanCard_Accepts()
    {
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff, decision.Action);
        Assert.True(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_EmptyDiffButFirstPass_DoesNotAccept()
    {
        // priorReissues == 0: there is no "follow-up" run yet, so the empty-diff
        // rule must not fire. Budget is intact too, so no loop-break at all.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 0, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_EmptyDiffButStateNotAcceptable_DoesNotAccept()
    {
        // An empty re-run on a card whose own close-out is NOT clean is not
        // evidence that nothing is open; the empty-diff accept must not fire.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: false);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
    }

    [Fact]
    public void Evaluate_NonEmptyDiffBudgetIntact_NoLoopBreak()
    {
        // Real follow-up work landed (HEAD changed) and budget remains: fall
        // through to the normal reissue path.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_BudgetSpentNonEmptyDiff_Escalates()
    {
        // Budget exhausted and the re-run is not empty: do not loop back to
        // 2-ready again; escalate to human review.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 2, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.Escalate, decision.Action);
        Assert.True(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_BudgetSpentButStateNotAcceptable_StillEscalates()
    {
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 3, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: false);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.Escalate, decision.Action);
    }

    [Fact]
    public void Evaluate_EmptyDiffAcceptTakesPrecedenceOverBudget()
    {
        // Both rules are eligible (budget spent AND empty clean re-run). The
        // empty-diff accept must win: an empty clean re-run is low-burden to
        // accept and should not be escalated as a "budget exhausted" failure.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 2, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff, decision.Action);
    }

    [Fact]
    public void Evaluate_DefaultHappyPath_NoLoopBreak()
    {
        // First pass, real diff, budget intact: the common case is no loop-break.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 0, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }
}
