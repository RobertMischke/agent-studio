using System.Text.RegularExpressions;

using AgentStudio.Docs;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Holds the code-rendered frame shells (AGT-2024, the seeded frame) to the same
/// bar the hand-authored EW-1 shells meet in
/// <see cref="EngineeringWorkstreamShellContentTests"/>: self-contained for the
/// script-disabled sandboxed iframe, themed for both light and dark, a bold
/// orientation layout, the overview navigating every area, and each area shell
/// carrying the cross-area rail with the current area lit. Rendering the real
/// output (not a fixture) keeps the seeder honest.
/// </summary>
public class EngineeringWorkstreamFrameContentTests
{
    public static IEnumerable<object[]> AllShellsEnglish()
    {
        yield return new object[] { EngineeringWorkstreamFrameContent.RenderOverview(EngineeringWorkstreamFrameLanguage.English) };
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            yield return new object[] { EngineeringWorkstreamFrameContent.RenderArea(area, EngineeringWorkstreamFrameLanguage.English) };
    }

    [Theory]
    [MemberData(nameof(AllShellsEnglish))]
    public void Shell_IsSelfContained_ForScriptDisabledSandbox(string html)
    {
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("src\\s*=", RegexOptions.IgnoreCase), html);
        Assert.Contains("<style>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllShellsEnglish))]
    public void Shell_DefinesDesignTokens_ForBothThemes(string html)
    {
        Assert.Contains(":root", html);
        Assert.Contains("--ew-bg", html);
        Assert.Contains("--ew-fg", html);
        Assert.Contains("--ew-accent", html);

        var lightIdx = html.IndexOf("@media (prefers-color-scheme: light)", StringComparison.OrdinalIgnoreCase);
        Assert.True(lightIdx > 0, "no light-theme media query");

        var dark = html[..lightIdx];
        var light = html[lightIdx..];
        Assert.Contains("--ew-bg", dark);
        Assert.Contains("--ew-fg", dark);
        Assert.Contains("--ew-bg", light);
        Assert.Contains("--ew-fg", light);
        // The two themes actually differ.
        Assert.DoesNotContain("--ew-bg: #11111b", light);
    }

    [Theory]
    [MemberData(nameof(AllShellsEnglish))]
    public void Shell_IsWellFormedOrientationLayout(string html)
    {
        Assert.StartsWith("<!doctype html>", html.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<body", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ew-hero", html);
        Assert.Contains("<h1>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ew-lock", html);

        // AGENTS.md: no em dashes in written artifacts (rendered or en-dash).
        Assert.DoesNotContain("—", html);
        Assert.DoesNotContain("–", html);
    }

    [Fact]
    public void OverviewShell_EnumeratesAllFiveAreas_InOrder()
    {
        var html = EngineeringWorkstreamFrameContent.RenderOverview(EngineeringWorkstreamFrameLanguage.English);

        var lastIndex = -1;
        foreach (var area in EngineeringWorkstreamFrame.Areas)
        {
            var slug = area.FolderRel["engineering-workstream/".Length..];
            Assert.Contains(area.Title, html);
            Assert.Contains(area.Purpose, html);
            Assert.Contains($"{slug}/", html);

            var at = html.IndexOf(area.Title, StringComparison.Ordinal);
            Assert.True(at > lastIndex, $"overview lists '{area.Title}' out of frame order");
            lastIndex = at;
        }
    }

    [Fact]
    public void AreaShells_CarryOrientationRail_WithCurrentAreaLit()
    {
        foreach (var self in EngineeringWorkstreamFrame.Areas)
        {
            var html = EngineeringWorkstreamFrameContent.RenderArea(self, EngineeringWorkstreamFrameLanguage.English);

            Assert.Contains(self.Title, html);
            Assert.Contains(self.Purpose, html);

            var rail = html.IndexOf("ew-rail", StringComparison.Ordinal);
            Assert.True(rail > 0, $"{self.Slug}: no orientation rail");
            foreach (var area in EngineeringWorkstreamFrame.Areas)
                Assert.Contains(area.Title, html[rail..]);

            var lit = Regex.Matches(html, Regex.Escape("ew-pill ew-pill--here")).Count;
            Assert.Equal(1, lit);
        }
    }
}
