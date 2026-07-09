using System.Text.RegularExpressions;

using AgentStudio.Docs;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the shipped Engineering Workstream orientation shells (slice EW-1) as
/// artifacts: the HTML landing pages under <c>docs/engineering-workstream/</c>.
/// The immutability rules are unit-tested in
/// <see cref="EngineeringWorkstreamFrameTests"/>; this suite verifies the parts of
/// the frame that live in the shells themselves and are otherwise only visible by
/// eye — the requirements the concept doc pins (§3):
///
/// <list type="bullet">
///   <item><b>Self-contained</b> — no scripts, external CSS/fonts/images, so each
///   shell renders safely in the wiki's script-disabled sandboxed iframe.</item>
///   <item><b>Both themes</b> — inline design tokens with a dark default plus a
///   <c>prefers-color-scheme: light</c> override (the iframe strips scripts, so
///   theming must be CSS-only).</item>
///   <item><b>Orientation layout + navigation</b> — the overview enumerates all
///   five areas in order; each area shell carries the "where you are" rail across
///   all five areas with the current one lit.</item>
/// </list>
///
/// Reading the real files (not fixtures) keeps the shells honest: renaming an
/// area, dropping a theme, or slipping in a script breaks a test here.
/// </summary>
public class EngineeringWorkstreamShellContentTests
{
    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found above test base directory.");
    }

    /// <summary>Resolves a wiki-root-relative shell path to the real docs/ file.</summary>
    private static string ShellPath(string wikiRel)
        => Path.Combine(RepoRoot(), "docs", wikiRel.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadShell(string wikiRel)
    {
        var path = ShellPath(wikiRel);
        Assert.True(File.Exists(path), $"Frame shell missing on disk: {wikiRel}");
        return File.ReadAllText(path);
    }

    /// <summary>The overview plus every area landing shell — the immutable pages.</summary>
    public static IEnumerable<object[]> AllShells()
    {
        yield return new object[] { EngineeringWorkstreamFrame.OverviewShellRel };
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            yield return new object[] { area.IndexShellRel };
    }

    // ---- the declared shells actually exist ----

    [Fact]
    public void EveryDeclaredShell_ExistsOnDisk()
    {
        Assert.True(File.Exists(ShellPath(EngineeringWorkstreamFrame.OverviewShellRel)),
            "overview shell missing");
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            Assert.True(File.Exists(ShellPath(area.IndexShellRel)),
                $"area shell missing: {area.IndexShellRel}");
    }

    // ---- self-contained for the sandboxed iframe ----

    [Theory]
    [MemberData(nameof(AllShells))]
    public void Shell_IsSelfContained_ForScriptDisabledSandbox(string wikiRel)
    {
        var html = ReadShell(wikiRel);

        // No scripts at all — the iframe disables them, and inline styling is the
        // only rendering path the frame relies on.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

        // No external resources: no linked stylesheets, no @import, no remote
        // fonts/images. Everything the shell needs is inline.
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("src\\s*=", RegexOptions.IgnoreCase), html);

        // Styling is inline.
        Assert.Contains("<style>", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- design tokens + both themes ----

    [Theory]
    [MemberData(nameof(AllShells))]
    public void Shell_DefinesDesignTokens_ForBothThemes(string wikiRel)
    {
        var html = ReadShell(wikiRel);

        // Design tokens live on :root and are namespaced --ew-*.
        Assert.Contains(":root", html);
        Assert.Contains("--ew-bg", html);
        Assert.Contains("--ew-fg", html);
        Assert.Contains("--ew-accent", html);

        // Dark is the default (declared before the light media query); light is the
        // prefers-color-scheme override. Both re-define the core surface/text tokens.
        var lightIdx = html.IndexOf("@media (prefers-color-scheme: light)", StringComparison.OrdinalIgnoreCase);
        Assert.True(lightIdx > 0, $"{wikiRel}: no light-theme media query");

        var dark = html[..lightIdx];
        var light = html[lightIdx..];
        Assert.Contains("--ew-bg", dark);
        Assert.Contains("--ew-fg", dark);
        Assert.Contains("--ew-bg", light);
        Assert.Contains("--ew-fg", light);

        // The two themes must actually differ (a light override that repeats the
        // dark values would defeat the point).
        Assert.DoesNotContain("--ew-bg: #11111b", light);
    }

    // ---- orientation layout scaffolding ----

    [Theory]
    [MemberData(nameof(AllShells))]
    public void Shell_IsWellFormedOrientationLayout(string wikiRel)
    {
        var html = ReadShell(wikiRel);

        Assert.StartsWith("<!doctype html>", html.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<body", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", html, StringComparison.OrdinalIgnoreCase);

        // The bold orientation layout: a hero band and a single H1 title.
        Assert.Contains("ew-hero", html);
        Assert.Contains("<h1>", html, StringComparison.OrdinalIgnoreCase);

        // The frame's immutability is stated in the shell itself.
        Assert.Contains("ew-lock", html);
    }

    // ---- overview navigates to all five areas ----

    [Fact]
    public void OverviewShell_EnumeratesAllFiveAreas_InOrder()
    {
        var html = ReadShell(EngineeringWorkstreamFrame.OverviewShellRel);

        var lastIndex = -1;
        foreach (var area in EngineeringWorkstreamFrame.Areas)
        {
            // Title, purpose, and the folder slug that addresses the area — the
            // reader can navigate from the overview to every area.
            var slug = area.FolderRel["engineering-workstream/".Length..];
            Assert.Contains(area.Title, html);
            Assert.Contains(area.Purpose, html);
            Assert.Contains($"{slug}/", html);

            var at = html.IndexOf(area.Title, StringComparison.Ordinal);
            Assert.True(at > lastIndex, $"overview lists '{area.Title}' out of frame order");
            lastIndex = at;
        }
    }

    // ---- every area shell carries the cross-area navigation rail ----

    [Theory]
    [MemberData(nameof(AllShells))]
    public void AreaShell_CarriesOrientationRail_WithCurrentAreaLit(string wikiRel)
    {
        if (wikiRel == EngineeringWorkstreamFrame.OverviewShellRel) return; // overview has its own grid

        var html = ReadShell(wikiRel);
        var self = EngineeringWorkstreamFrame.Areas.Single(a => a.IndexShellRel == wikiRel);

        // Its own identity: title and purpose.
        Assert.Contains(self.Title, html);
        Assert.Contains(self.Purpose, html);

        // The rail lists all five areas so the reader always knows the whole frame.
        var rail = html.IndexOf("ew-rail", StringComparison.Ordinal);
        Assert.True(rail > 0, $"{wikiRel}: no orientation rail");
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            Assert.Contains(area.Title, html[rail..]);

        // Exactly one area pill is marked as "here" — the current one. (The bare
        // ".ew-pill--here" selector also appears in the CSS, so match the element's
        // full class token to count only the rendered pill.)
        var lit = Regex.Matches(html, Regex.Escape("ew-pill ew-pill--here")).Count;
        Assert.Equal(1, lit);
    }

    // ---- the shells on disk match exactly the frame's declared identity ----

    [Fact]
    public void OnDiskShellSet_MatchesDeclaredFrameIdentity()
    {
        var frameDir = Path.Combine(RepoRoot(), "docs", EngineeringWorkstreamFrame.FrameRootRel);
        Assert.True(Directory.Exists(frameDir), "frame folder missing on disk");

        var declared = new[] { EngineeringWorkstreamFrame.OverviewShellRel }
            .Concat(EngineeringWorkstreamFrame.Areas.Select(a => a.IndexShellRel))
            .Select(rel => Path.GetFullPath(ShellPath(rel)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var onDisk = Directory.EnumerateFiles(frameDir, "*.html", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(declared, onDisk);
    }
}
