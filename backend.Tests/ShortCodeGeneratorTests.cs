using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F45a — covers the four documented derivation cases plus collision
/// resolution and the format validator. The seed values pinned here
/// mirror the examples in the F45 prompt (Runbook → RUN, Agent Task
/// Processor → ATP) so a regression in any of those would surface as
/// a contract failure.
/// </summary>
public class ShortCodeGeneratorTests
{
    [Theory]
    [InlineData("Runbook", "RUN")]
    [InlineData("runbook", "RUN")]
    [InlineData("Agent Task Processor", "ATP")]
    [InlineData("Agent Software Studio Project", "ASS")]
    [InlineData("a-b-c", "ABC")]
    public void Derive_FollowsContract_ForCommonShapes(string displayName, string expected)
    {
        var code = ShortCodeGenerator.Derive(displayName, []);
        Assert.Equal(expected, code);
    }

    [Fact]
    public void Derive_SuffixesNumeric_OnCollision()
    {
        var first = ShortCodeGenerator.Derive("Agent Task Processor", []);
        var second = ShortCodeGenerator.Derive("Agent Task Processor", [first]);
        var third = ShortCodeGenerator.Derive("Agent Task Processor", [first, second]);

        Assert.Equal("ATP", first);
        Assert.Equal("ATP2", second);
        Assert.Equal("ATP3", third);
    }

    [Fact]
    public void Derive_SingleShortWord_PadsTo2Chars()
    {
        // "Hi" -> only 2 letters, no second word; "HI" is 2 chars,
        // which satisfies the validator's minimum.
        var code = ShortCodeGenerator.Derive("Hi", []);
        Assert.True(ShortCodeGenerator.ValidateFormat(code), $"Expected valid 2-char code, got '{code}'");
        Assert.Equal("HI", code);
    }

    [Fact]
    public void Derive_EmptyInput_FallsBackToProj()
    {
        var code = ShortCodeGenerator.Derive("", []);
        Assert.Equal("PROJ", code);
    }

    [Fact]
    public void Derive_NonLatinInput_FallsBackToProj()
    {
        // No A-Z letters survive AlnumOnly's allow-list reduction.
        var code = ShortCodeGenerator.Derive("こんにちは", []);
        Assert.Equal("PROJ", code);
    }

    [Theory]
    [InlineData("ATP", true)]
    [InlineData("RUN", true)]
    [InlineData("PROJ12", true)]
    [InlineData("A", false)] // too short
    [InlineData("ABCDEFG", false)] // too long
    [InlineData("atp", false)] // lower-case
    [InlineData("1ABC", false)] // starts with digit
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ValidateFormat_MatchesPublishedConstraint(string? code, bool valid)
    {
        Assert.Equal(valid, ShortCodeGenerator.ValidateFormat(code));
    }
}
