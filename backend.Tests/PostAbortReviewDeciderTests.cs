
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure abort-review core: the verdict parser and the budget-aware
/// decider. These are the load-bearing pieces of the "Intelligente
/// Abbruch-Bewertung" feature - the model classifies, this code decides. The
/// bug class guarded here: a confidently-wrong (or unparseable) model reply
/// must never let the orchestrator rerun past its budget, and an absent
/// verdict must fail closed to human review rather than silently accept.
/// </summary>
public class PostAbortReviewDeciderTests
{
    // ---- Decider table -----------------------------------------------------

    [Fact]
    public void NullVerdict_FailsClosed_ToHuman()
    {
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(null, rerunBudgetRemaining: 5));
    }

    [Fact]
    public void Accept_IsHonoured_RegardlessOfBudget()
    {
        var verdict = new PostAbortReviewVerdict(false, PostAbortRecommendation.Accept, "enough work landed", 0.9);
        Assert.Equal(PostAbortAction.AcceptAndContinue, PostAbortReviewDecider.Decide(verdict, 0));
        Assert.Equal(PostAbortAction.AcceptAndContinue, PostAbortReviewDecider.Decide(verdict, 3));
    }

    [Fact]
    public void HumanReview_Recommendation_Escalates_EvenWithBudget()
    {
        var verdict = new PostAbortReviewVerdict(true, PostAbortRecommendation.HumanReview, "real dead end", 0.8);
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(verdict, 5));
    }

    [Theory]
    [InlineData(2, PostAbortAction.Rerun)]
    [InlineData(1, PostAbortAction.Rerun)]
    [InlineData(0, PostAbortAction.EscalateHuman)]
    [InlineData(-1, PostAbortAction.EscalateHuman)]
    public void Rerun_RespectsBudget(int budget, PostAbortAction expected)
    {
        var verdict = new PostAbortReviewVerdict(false, PostAbortRecommendation.Rerun, "watchdog tripped on live build", 0.7);
        Assert.Equal(expected, PostAbortReviewDecider.Decide(verdict, budget));
    }

    [Theory]
    [InlineData(2, PostAbortAction.RerunWithStrongerFraming)]
    [InlineData(0, PostAbortAction.EscalateHuman)]
    public void StrongerReissue_RespectsBudget(int budget, PostAbortAction expected)
    {
        var verdict = new PostAbortReviewVerdict(false, PostAbortRecommendation.StrongerReissue, "agent was looping", 0.6);
        Assert.Equal(expected, PostAbortReviewDecider.Decide(verdict, budget));
    }

    // ---- Parser: canonical sentinel ---------------------------------------

    [Fact]
    public void Parse_CanonicalSentinel_AllFields()
    {
        var reply = "Looking at the evidence, the build was still running.\n\n" +
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=0.82; reason=ng serve was alive, watchdog mis-fired]]";
        var v = PostAbortReviewVerdictParsing.Parse(reply);
        Assert.NotNull(v);
        Assert.False(v!.LegitimateAbort);
        Assert.Equal(PostAbortRecommendation.Rerun, v.Recommendation);
        Assert.Equal(0.82, v.Confidence, 3);
        Assert.Contains("watchdog", v.Reasoning);
    }

    [Theory]
    [InlineData("rerun", PostAbortRecommendation.Rerun)]
    [InlineData("retry", PostAbortRecommendation.Rerun)]
    [InlineData("stronger-reissue", PostAbortRecommendation.StrongerReissue)]
    [InlineData("stronger_reissue", PostAbortRecommendation.StrongerReissue)]
    [InlineData("reissue", PostAbortRecommendation.StrongerReissue)]
    [InlineData("human-review", PostAbortRecommendation.HumanReview)]
    [InlineData("human", PostAbortRecommendation.HumanReview)]
    [InlineData("escalate", PostAbortRecommendation.HumanReview)]
    [InlineData("accept", PostAbortRecommendation.Accept)]
    public void Parse_RecommendationSynonyms(string token, PostAbortRecommendation expected)
    {
        var v = PostAbortReviewVerdictParsing.Parse(
            $"[[ABORT_REVIEW: legitimate=false; recommendation={token}; confidence=0.5; reason=x]]");
        Assert.NotNull(v);
        Assert.Equal(expected, v!.Recommendation);
    }

    [Fact]
    public void Parse_GermanFieldAliases()
    {
        var v = PostAbortReviewVerdictParsing.Parse(
            "[[ABORT_REVIEW: legitimer_abbruch=true; empfehlung=human-review; confidence=90%; begruendung=echte Sackgasse]]");
        Assert.NotNull(v);
        Assert.True(v!.LegitimateAbort);
        Assert.Equal(PostAbortRecommendation.HumanReview, v.Recommendation);
        Assert.Equal(0.9, v.Confidence, 3);
        Assert.Contains("Sackgasse", v.Reasoning);
    }

    [Fact]
    public void Parse_ConfidenceClampsAndToleratesPercent()
    {
        Assert.Equal(1.0, PostAbortReviewVerdictParsing.Parse(
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=1.5; reason=x]]")!.Confidence, 3);
        Assert.Equal(0.8, PostAbortReviewVerdictParsing.Parse(
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=80; reason=x]]")!.Confidence, 3);
    }

    [Fact]
    public void Parse_DefaultsConfidenceWhenAbsent()
    {
        var v = PostAbortReviewVerdictParsing.Parse(
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; reason=x]]");
        Assert.NotNull(v);
        Assert.Equal(0.5, v!.Confidence, 3);
    }

    [Fact]
    public void Parse_LastSentinelWins()
    {
        var reply = "[[ABORT_REVIEW: legitimate=true; recommendation=human-review; confidence=0.3; reason=first]]\n" +
            "On reflection:\n" +
            "[[ABORT_REVIEW: legitimate=false; recommendation=rerun; confidence=0.9; reason=second]]";
        var v = PostAbortReviewVerdictParsing.Parse(reply);
        Assert.Equal(PostAbortRecommendation.Rerun, v!.Recommendation);
        Assert.Contains("second", v.Reasoning);
    }

    [Fact]
    public void Parse_ToleratesCodeFenceWrapper()
    {
        var reply = "```\n[[ABORT_REVIEW: legitimate=false; recommendation=accept; confidence=0.7; reason=work landed]]\n```";
        var v = PostAbortReviewVerdictParsing.Parse(reply);
        Assert.NotNull(v);
        Assert.Equal(PostAbortRecommendation.Accept, v!.Recommendation);
    }

    // ---- Parser: tolerant fallback ----------------------------------------

    [Fact]
    public void Parse_FallbackLine_NoSentinel()
    {
        var reply = "The agent clearly looped on the same edit.\n" +
            "Recommendation: stronger-reissue\n" +
            "Reason: it kept re-reading the same file";
        var v = PostAbortReviewVerdictParsing.Parse(reply);
        Assert.NotNull(v);
        Assert.Equal(PostAbortRecommendation.StrongerReissue, v!.Recommendation);
        Assert.Contains("re-reading", v.Reasoning);
    }

    [Fact]
    public void Parse_FallbackHumanReview_SetsLegitimateAbort()
    {
        var v = PostAbortReviewVerdictParsing.Parse("recommendation = human-review");
        Assert.NotNull(v);
        Assert.True(v!.LegitimateAbort);
        Assert.Equal(PostAbortRecommendation.HumanReview, v.Recommendation);
    }

    // ---- Parser: unparseable -> null (fail closed) ------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("The model wandered off and never gave a recommendation.")]
    [InlineData("[[ABORT_REVIEW: legitimate=false; recommendation=banana; confidence=0.5]]")]
    public void Parse_ReturnsNull_WhenUnrecoverable(string? reply)
    {
        Assert.Null(PostAbortReviewVerdictParsing.Parse(reply));
    }

    [Fact]
    public void Parse_Null_ThenDecider_Escalates()
    {
        // End-to-end of the fail-closed contract: an unparseable reply must
        // not rerun, it must route to a human even with budget to spare.
        var verdict = PostAbortReviewVerdictParsing.Parse("no verdict here");
        Assert.Equal(PostAbortAction.EscalateHuman, PostAbortReviewDecider.Decide(verdict, 5));
    }
}
