using AgentStudio.Docs;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the reusable ensure-frame primitive (AGT-2024): the idempotent
/// self-provisioning of the Workstream frame into a target project's
/// <c>docs/</c> tree, and the public/localized language resolution. Uses a real
/// temp docs tree (neighbour pattern) rather than fixtures so the on-disk
/// behaviour - create missing, never overwrite, leave foreign files alone - is
/// exercised end to end.
/// </summary>
public sealed class EngineeringWorkstreamFrameSeederTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ew-frame-seeder-tests", Guid.NewGuid().ToString("N"));

    private string DocsRoot(string project) => Path.Combine(_root, project, "docs");

    private static string FrameRoot(string docsRoot) =>
        Path.Combine(docsRoot, "engineering-workstream");

    // ---- empty docs -> full frame ----

    [Fact]
    public void EnsureFrame_EmptyDocs_CreatesWholeFrame()
    {
        var docs = DocsRoot("empty");

        var result = EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);

        Assert.True(result.CreatedAnything);
        Assert.Equal(6, result.Created.Count); // overview + five area shells
        Assert.Empty(result.Existing);
        Assert.Empty(result.Failed);

        Assert.True(File.Exists(Path.Combine(FrameRoot(docs), "00-overview.html")));
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            Assert.True(
                File.Exists(Path.Combine(docs, area.IndexShellRel.Replace('/', Path.DirectorySeparatorChar))),
                $"missing area shell {area.IndexShellRel}");

        // The overview lists every area, in frame order.
        var overview = File.ReadAllText(Path.Combine(FrameRoot(docs), "00-overview.html"));
        var last = -1;
        foreach (var area in EngineeringWorkstreamFrame.Areas)
        {
            var at = overview.IndexOf(area.Title, StringComparison.Ordinal);
            Assert.True(at > last, $"overview lists '{area.Title}' out of order");
            last = at;
        }
    }

    // ---- partial frame -> only the missing parts are added ----

    [Fact]
    public void EnsureFrame_PartialFrame_CompletesOnlyMissingWithoutOverwriting()
    {
        var docs = DocsRoot("partial");
        Directory.CreateDirectory(FrameRoot(docs));
        var overviewPath = Path.Combine(FrameRoot(docs), "00-overview.html");
        const string custom = "<!doctype html><html><body><h1>Hand edited overview</h1></body></html>";
        File.WriteAllText(overviewPath, custom);

        var result = EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);

        // Only the five area shells are new; the overview already existed.
        Assert.Equal(5, result.Created.Count);
        Assert.Single(result.Existing);
        Assert.Equal(EngineeringWorkstreamFrame.OverviewShellRel, result.Existing[0]);
        Assert.Empty(result.Failed);

        // The pre-existing overview content is preserved verbatim (never overwritten).
        Assert.Equal(custom, File.ReadAllText(overviewPath));
        foreach (var area in EngineeringWorkstreamFrame.Areas)
            Assert.True(File.Exists(Path.Combine(docs, area.IndexShellRel.Replace('/', Path.DirectorySeparatorChar))));
    }

    // ---- second run -> idempotent no-op ----

    [Fact]
    public void EnsureFrame_SecondRun_IsIdempotentNoOp()
    {
        var docs = DocsRoot("idempotent");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);
        var areaShell = Path.Combine(docs,
            EngineeringWorkstreamFrame.Areas[0].IndexShellRel.Replace('/', Path.DirectorySeparatorChar));
        var firstBytes = File.ReadAllText(areaShell);

        var second = EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);

        Assert.False(second.CreatedAnything);
        Assert.Empty(second.Created);
        Assert.Equal(6, second.Existing.Count);
        Assert.Empty(second.Failed);
        // A no-op run does not rewrite the shell.
        Assert.Equal(firstBytes, File.ReadAllText(areaShell));
    }

    // ---- foreign files under docs/ are never touched ----

    [Fact]
    public void EnsureFrame_LeavesForeignFilesUntouched()
    {
        var docs = DocsRoot("foreign");
        Directory.CreateDirectory(docs);
        var foreignTop = Path.Combine(docs, "README.md");
        File.WriteAllText(foreignTop, "# Project docs\n");
        // A regular subpage inside a frame area folder must survive too.
        var areaFolder = Path.Combine(docs, "engineering-workstream", "40-decision-log");
        Directory.CreateDirectory(areaFolder);
        var subpage = Path.Combine(areaFolder, "adr-0001.md");
        File.WriteAllText(subpage, "# ADR 1\n");

        EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);

        Assert.Equal("# Project docs\n", File.ReadAllText(foreignTop));
        Assert.Equal("# ADR 1\n", File.ReadAllText(subpage));
        // ...and the area's own landing shell was still added beside the subpage.
        Assert.True(File.Exists(Path.Combine(areaFolder, "index.html")));
    }

    // ---- language: public -> English, localized -> German ----

    [Fact]
    public void EnsureFrame_English_RendersEnglishFrame()
    {
        var docs = DocsRoot("en");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);

        var overview = File.ReadAllText(Path.Combine(FrameRoot(docs), "00-overview.html"));
        Assert.Contains("The development story", overview);
        Assert.Contains("Fixed frame", overview);
        Assert.Contains("lang=\"en\"", overview);
        Assert.DoesNotContain("Fester Rahmen", overview);
        Assert.DoesNotContain("Wo du bist", overview);
    }

    [Fact]
    public void EnsureFrame_German_RendersLocalizedFrame()
    {
        var docs = DocsRoot("de");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.German);

        var overview = File.ReadAllText(Path.Combine(FrameRoot(docs), "00-overview.html"));
        Assert.Contains("Fester Rahmen", overview);
        Assert.Contains("lang=\"de\"", overview);
        Assert.DoesNotContain("Fixed frame", overview);

        var area = File.ReadAllText(Path.Combine(FrameRoot(docs), "10-current-development-state", "index.html"));
        Assert.Contains("Wo du bist", area);
        // The five area identities stay their fixed English names in every language.
        Assert.Contains("Current Development State", area);
    }

    // ---- resolver: explicit override wins, heuristic default is English ----

    [Theory]
    [InlineData(true, EngineeringWorkstreamFrameLanguage.English)]
    [InlineData(false, EngineeringWorkstreamFrameLanguage.German)]
    [InlineData(null, EngineeringWorkstreamFrameLanguage.English)]
    public void Resolve_MapsPublicFlagToLanguage(bool? isPublic, EngineeringWorkstreamFrameLanguage expected)
    {
        Assert.Equal(expected, WorkstreamFrameLanguageResolver.Resolve("coding-agent-runner", isPublic));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
