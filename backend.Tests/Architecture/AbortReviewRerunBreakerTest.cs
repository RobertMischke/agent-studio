
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture-level lock for the loop-inventory entry
/// <c>abort-review.rerun-per-job</c> (the "Intelligente Abbruch-Bewertung"
/// rerun loop):
///
/// <list type="number">
///   <item>The decider and its budget constant live in code
///   (<see cref="PostAbortReviewDecider"/>).</item>
///   <item>The breaker behaviour is pinned: an exhausted budget escalates
///   to human review instead of re-running forever, and a null verdict
///   (CLI failure / unparseable reply) fails closed the same way.</item>
///   <item>The inventory entry references this exact test file path.</item>
/// </list>
///
/// <para>
/// ADR-0032 rule: every loop class is registered with a breaker. The
/// abort-review step keeps the pipeline alive by re-running when the model
/// says the abort was not legitimate; this budget is the breaker that stops
/// a confidently-wrong model from spinning the pipeline indefinitely.
/// </para>
/// </summary>
public class AbortReviewRerunBreakerTest
{
    [Fact]
    public void DeciderType_Exists()
    {
        Assert.NotNull(typeof(PostAbortReviewDecider));
    }

    [Fact]
    public void DefaultRerunBudget_HasExpectedValue()
    {
        // Pin the default documented in docs/loop-inventory.md
        // (abort-review.rerun-per-job). Change both in the same commit when
        // tuning the budget.
        Assert.Equal(2, PostAbortReviewDecider.DefaultRerunBudget);
    }

    [Theory]
    [InlineData(PostAbortRecommendation.Rerun)]
    [InlineData(PostAbortRecommendation.StrongerReissue)]
    public void BudgetExhausted_Escalates_NotRerun(PostAbortRecommendation rec)
    {
        var verdict = new PostAbortReviewVerdict(false, rec, "model wants another go", 0.95);
        // With the budget spent, no rerun recommendation may keep the loop
        // alive - it must hand off to a human.
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(verdict, rerunBudgetRemaining: 0));
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(verdict, rerunBudgetRemaining: -1));
    }

    [Fact]
    public void NullVerdict_FailsClosed_ToHuman()
    {
        // CLI failure / unparseable reply -> no trustworthy verdict -> human,
        // even with budget to spare. The loop never advances on a guess.
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(null, rerunBudgetRemaining: 5));
    }

    [Fact]
    public void BudgetMonotonicallyConvergesToEscalation()
    {
        // Walk the budget down from the default to zero: each rerun consumes
        // one unit, and the terminal state is always escalation. This is the
        // structural proof the loop cannot run unbounded.
        var verdict = new PostAbortReviewVerdict(false, PostAbortRecommendation.Rerun, "retry", 0.8);
        for (var budget = PostAbortReviewDecider.DefaultRerunBudget; budget > 0; budget--)
            Assert.Equal(PostAbortAction.Rerun, PostAbortReviewDecider.Decide(verdict, budget));
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(verdict, 0));
    }
}
