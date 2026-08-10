using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the filename-suffix grammar that decides a screenshot's provenance
/// label. The label is the trust signal a reviewer reads next to each
/// thumbnail, so the boundary rules matter: a single dash in the base name
/// must not be mistaken for a source segment, and an unrecognised suffix must
/// stay <c>unlabeled</c> rather than guessing. Composite part extraction is
/// covered because the composite case carries the most surface area.
/// </summary>
public class ScreenshotSourceParserTests
{
    [Theory]
    [InlineData("dashboard--real.png", ScreenshotSources.Real)]
    [InlineData("dashboard--mocked.png", ScreenshotSources.Mocked)]
    [InlineData("DASHBOARD--REAL.PNG", ScreenshotSources.Real)] // case-insensitive head
    [InlineData("before-after--composite.png", ScreenshotSources.Composite)]
    [InlineData("landing-board--pinned.png", ScreenshotSources.Pinned)]
    public void Parse_RecognisedSuffix_YieldsSource(string fileName, string expected)
    {
        var info = ScreenshotSourceParser.Parse(fileName);
        Assert.Equal(expected, info.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dashboard.png")]            // no boundary at all
    [InlineData("before-after.png")]          // single dash is part of the base name
    [InlineData("dashboard--.png")]           // empty segment
    [InlineData("dashboard--bogus.png")]      // unrecognised head token
    public void Parse_NoRecognisedSuffix_IsUnlabeled(string? fileName)
    {
        var info = ScreenshotSourceParser.Parse(fileName);
        Assert.Equal(ScreenshotSources.Unlabeled, info.Source);
        Assert.Empty(info.Parts);
    }

    [Fact]
    public void Parse_NonCompositeSources_HaveNoParts()
    {
        Assert.Empty(ScreenshotSourceParser.Parse("a--real.png").Parts);
        Assert.Empty(ScreenshotSourceParser.Parse("a--mocked.png").Parts);
        Assert.Empty(ScreenshotSourceParser.Parse("a--pinned.png").Parts);
    }

    [Fact]
    public void Parse_CompositeWithParts_CollectsRealAndMockedInOrder()
    {
        var info = ScreenshotSourceParser.Parse("before-after--composite-real-mocked.png");

        Assert.Equal(ScreenshotSources.Composite, info.Source);
        Assert.Equal([ScreenshotSources.Real, ScreenshotSources.Mocked], info.Parts);
    }

    [Fact]
    public void Parse_CompositeWithoutParts_HasEmptyParts()
    {
        var info = ScreenshotSourceParser.Parse("x--composite.png");

        Assert.Equal(ScreenshotSources.Composite, info.Source);
        Assert.Empty(info.Parts);
    }

    [Fact]
    public void Parse_CompositeIgnoresUnknownPartTokens()
    {
        // Only real/mocked are recognised as part sources; noise is dropped.
        var info = ScreenshotSourceParser.Parse("x--composite-real-wat-mocked.png");

        Assert.Equal(ScreenshotSources.Composite, info.Source);
        Assert.Equal([ScreenshotSources.Real, ScreenshotSources.Mocked], info.Parts);
    }

    [Fact]
    public void Parse_OnlyLastBoundaryIntroducesSource()
    {
        // A base name may itself contain "--"; only the final boundary counts.
        var info = ScreenshotSourceParser.Parse("step--one--mocked.png");
        Assert.Equal(ScreenshotSources.Mocked, info.Source);
    }

    [Fact]
    public void Parse_ToleratesMissingExtension()
    {
        var info = ScreenshotSourceParser.Parse("dashboard--real");
        Assert.Equal(ScreenshotSources.Real, info.Source);
    }
}
