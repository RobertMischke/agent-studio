using OrchestratorApi.Services.Review;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pure-parser tests for the quality-grade reviewer (ASS-1657). Mirrors the
/// tolerance of <see cref="AspectVerdictParsing"/>: the canonical
/// <c>[[CODE_REVIEW_GRADE: ...]]</c> sentinel is preferred, a "Grade: X" line
/// is the fallback, and an unrecoverable reply returns null so the caller
/// applies a deterministic grade-C rather than silently waving the work
/// through.
/// </summary>
public class CodeReviewGradeParsingTests
{
    [Theory]
    [InlineData("A", CodeReviewGrade.A)]
    [InlineData("B", CodeReviewGrade.B)]
    [InlineData("C", CodeReviewGrade.C)]
    [InlineData("D", CodeReviewGrade.D)]
    public void ParseGrade_ReadsCanonicalSentinel(string token, CodeReviewGrade expected)
    {
        var reply = $"Some justification.\n[[CODE_REVIEW_GRADE: grade={token}; summary=Short reason.]]\n[[TASK_DONE]]";

        var parsed = CodeReviewGradeParsing.ParseGrade(reply);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.Grade);
        Assert.Equal("Short reason.", parsed.Value.Summary);
    }

    [Fact]
    public void ParseGrade_TakesLastSentinel_WhenModelEmitsTwo()
    {
        var reply = "[[CODE_REVIEW_GRADE: grade=D; summary=first]]\n[[CODE_REVIEW_GRADE: grade=B; summary=final]]";

        var parsed = CodeReviewGradeParsing.ParseGrade(reply);

        Assert.Equal(CodeReviewGrade.B, parsed!.Value.Grade);
        Assert.Equal("final", parsed.Value.Summary);
    }

    [Fact]
    public void ParseGrade_FallsBackToGradeLine_WhenSentinelMissing()
    {
        var reply = "I reviewed the diff.\n**Grade:** C\nThe work is half-done.";

        var parsed = CodeReviewGradeParsing.ParseGrade(reply);

        Assert.NotNull(parsed);
        Assert.Equal(CodeReviewGrade.C, parsed!.Value.Grade);
    }

    [Fact]
    public void ParseGrade_ReturnsNull_ForUnparseableReply()
    {
        Assert.Null(CodeReviewGradeParsing.ParseGrade("No verdict here."));
        Assert.Null(CodeReviewGradeParsing.ParseGrade(""));
        Assert.Null(CodeReviewGradeParsing.ParseGrade("   "));
    }

    [Fact]
    public void ParseGrade_IgnoresFencedCodeBlocks()
    {
        // A grade letter that only appears inside a ``` fence (e.g. example
        // text) must not be mistaken for the verdict; the real sentinel wins.
        var reply = "```\nGrade: A (example)\n```\n[[CODE_REVIEW_GRADE: grade=D; summary=actually broken]]";

        var parsed = CodeReviewGradeParsing.ParseGrade(reply);

        Assert.Equal(CodeReviewGrade.D, parsed!.Value.Grade);
    }

    [Theory]
    [InlineData(CodeReviewGrade.A, "code-review:grade-a")]
    [InlineData(CodeReviewGrade.B, "code-review:grade-b")]
    [InlineData(CodeReviewGrade.C, "code-review:grade-c")]
    [InlineData(CodeReviewGrade.D, "code-review:grade-d")]
    public void TagFor_NamespacesEachGrade(CodeReviewGrade grade, string expectedTag)
    {
        Assert.Equal(expectedTag, CodeReviewGradeParsing.TagFor(grade));
    }

    [Fact]
    public void AllTags_AreTheFourGradeTags()
    {
        Assert.Equal(
            new[] { "code-review:grade-a", "code-review:grade-b", "code-review:grade-c", "code-review:grade-d" },
            CodeReviewGradeParsing.AllTags);
    }

    [Theory]
    [InlineData(CodeReviewGrade.A, AspectStatus.Pass)]
    [InlineData(CodeReviewGrade.B, AspectStatus.Pass)]
    [InlineData(CodeReviewGrade.C, AspectStatus.Concerns)]
    [InlineData(CodeReviewGrade.D, AspectStatus.Block)]
    public void ToAspectStatus_MapsGradeOntoVerdictSeverity(CodeReviewGrade grade, AspectStatus expected)
    {
        Assert.Equal(expected, CodeReviewGradeParsing.ToAspectStatus(grade));
    }

    [Theory]
    [InlineData("a", CodeReviewGrade.A)]
    [InlineData(" B ", CodeReviewGrade.B)]
    [InlineData("\"C\"", CodeReviewGrade.C)]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void TokenToGrade_IsTolerant(string? token, CodeReviewGrade? expected)
    {
        Assert.Equal(expected, CodeReviewGradeParsing.TokenToGrade(token));
    }
}
