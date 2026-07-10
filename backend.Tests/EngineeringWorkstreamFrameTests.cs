using AgentStudio.Docs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the fixed Engineering Workstream frame (slice EW-1): the pure frame
/// identity + immutability rules in <see cref="EngineeringWorkstreamFrame"/>, and
/// their effect on the wiki surface — the tree's per-node <c>Immutable</c> flag
/// and the content lock in <see cref="ProjectDocsService.WriteWikiFile"/>.
/// </summary>
public class EngineeringWorkstreamFrameTests : IDisposable
{
    private readonly string _tempDir;

    public EngineeringWorkstreamFrameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ew-frame-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Areas (pure) ----

    [Fact]
    public void Areas_AreTheFiveFixedAreas_InOrder()
    {
        var titles = EngineeringWorkstreamFrame.Areas.Select(a => a.Title).ToArray();

        Assert.Equal(
            new[]
            {
                "Current Development State",
                "Development Signals",
                "System Knowledge",
                "Decision Log",
                "Workstream Log",
            },
            titles);

        // Folder slugs carry an ascending numeric order prefix and live under the root.
        Assert.Equal("engineering-workstream/10-current-development-state",
            EngineeringWorkstreamFrame.Areas[0].FolderRel);
        Assert.Equal("engineering-workstream/50-workstream-log/index.html",
            EngineeringWorkstreamFrame.Areas[4].IndexShellRel);
    }

    // ---- IsStructural / IsContentLocked (pure) ----

    [Theory]
    [InlineData("engineering-workstream")]                                   // frame root
    [InlineData("engineering-workstream/10-current-development-state")]      // area folder
    [InlineData("engineering-workstream/50-workstream-log")]                 // area folder
    [InlineData("engineering-workstream/00-overview.html")]                  // overview shell
    [InlineData("engineering-workstream/30-system-knowledge/index.html")]    // area shell
    public void IsStructural_TrueForFrameFoldersAndShells(string rel)
    {
        Assert.True(EngineeringWorkstreamFrame.IsStructural(rel));
    }

    [Theory]
    [InlineData("engineering-workstream/40-decision-log/adr-0001.md")]       // subpage
    [InlineData("engineering-workstream/20-development-signals/notes/x.md")] // deep subpage
    [InlineData("architecture/model.md")]                                    // unrelated doc
    [InlineData("engineering-workstream-other/index.html")]                  // prefix-only lookalike
    [InlineData("")]
    public void IsStructural_FalseForSubpagesAndOutsiders(string rel)
    {
        Assert.False(EngineeringWorkstreamFrame.IsStructural(rel));
    }

    [Fact]
    public void IsContentLocked_OnlyLandingShells()
    {
        Assert.True(EngineeringWorkstreamFrame.IsContentLocked("engineering-workstream/00-overview.html"));
        Assert.True(EngineeringWorkstreamFrame.IsContentLocked("engineering-workstream/40-decision-log/index.html"));
        // Folders are structurally locked but not content-locked; subpages are neither.
        Assert.False(EngineeringWorkstreamFrame.IsContentLocked("engineering-workstream/40-decision-log"));
        Assert.False(EngineeringWorkstreamFrame.IsContentLocked("engineering-workstream/40-decision-log/adr-0001.md"));
    }

    [Theory]
    [InlineData("engineering-workstream\\00-overview.html")]  // backslashes
    [InlineData("/engineering-workstream/00-overview.html")]  // leading slash
    [InlineData("engineering-workstream/00-overview.html/")]  // trailing slash
    [InlineData("Engineering-Workstream/00-Overview.HTML")]   // case-insensitive
    public void IsStructural_NormalizesPathShape(string rel)
    {
        Assert.True(EngineeringWorkstreamFrame.IsStructural(rel));
    }

    [Fact]
    public void IsWithinFrame_CoversRootAndDescendants_ButNotLookalikes()
    {
        Assert.True(EngineeringWorkstreamFrame.IsWithinFrame("engineering-workstream"));
        Assert.True(EngineeringWorkstreamFrame.IsWithinFrame("engineering-workstream/40-decision-log/adr.md"));
        Assert.False(EngineeringWorkstreamFrame.IsWithinFrame("engineering-workstream-other/x.md"));
        Assert.False(EngineeringWorkstreamFrame.IsWithinFrame("architecture"));
    }

    // ---- GetWikiTree marks frame nodes immutable ----

    [Fact]
    public void GetWikiTree_MarksFrameFoldersAndShellsImmutable_SubpagesMutable()
    {
        var projectRoot = Path.Combine(_tempDir, "frame-proj");
        SeedFrame(projectRoot);
        // A regular subpage under an area folder.
        File.WriteAllText(
            Path.Combine(projectRoot, "docs", "engineering-workstream", "40-decision-log", "adr-0001.md"),
            "# ADR 1\n");

        var docs = BuildDocsService(("Frame", projectRoot));
        var tree = docs.GetWikiTree("Frame");
        Assert.NotNull(tree);

        var root = FindNode(tree!.Root, "engineering-workstream");
        Assert.NotNull(root);
        Assert.True(root!.Immutable);

        var overview = FindNode(tree.Root, "engineering-workstream/00-overview.html");
        Assert.NotNull(overview);
        Assert.True(overview!.Immutable);

        var areaFolder = FindNode(tree.Root, "engineering-workstream/40-decision-log");
        Assert.NotNull(areaFolder);
        Assert.True(areaFolder!.Immutable);

        var areaShell = FindNode(tree.Root, "engineering-workstream/40-decision-log/index.html");
        Assert.NotNull(areaShell);
        Assert.True(areaShell!.Immutable);

        var subpage = FindNode(tree.Root, "engineering-workstream/40-decision-log/adr-0001.md");
        Assert.NotNull(subpage);
        Assert.False(subpage!.Immutable);
    }

    // ---- frame root display name + top pin ----

    [Fact]
    public void DisplayTitle_RelabelsFrameRootToWorkstream_LeavesOthersDefault()
    {
        Assert.Equal("Workstream", EngineeringWorkstreamFrame.DisplayTitle("engineering-workstream"));
        Assert.Null(EngineeringWorkstreamFrame.DisplayTitle("engineering-workstream/40-decision-log"));
        Assert.Null(EngineeringWorkstreamFrame.DisplayTitle("architecture"));
        Assert.True(EngineeringWorkstreamFrame.IsFrameRoot("engineering-workstream"));
        Assert.False(EngineeringWorkstreamFrame.IsFrameRoot("engineering-workstream/40-decision-log"));
    }

    [Fact]
    public void GetWikiTree_PinsWorkstreamFrameFirst_WithWorkstreamDisplayName()
    {
        var projectRoot = Path.Combine(_tempDir, "order-proj");
        SeedFrame(projectRoot);
        // A sibling top-level docs folder that sorts before "engineering-workstream"
        // alphabetically - it must still land after the pinned frame root.
        var arch = Path.Combine(projectRoot, "docs", "architecture");
        Directory.CreateDirectory(arch);
        File.WriteAllText(Path.Combine(arch, "index.md"), "# Architecture\n");

        var docs = BuildDocsService(("Order", projectRoot));
        var tree = docs.GetWikiTree("Order");
        Assert.NotNull(tree);

        Assert.Equal("engineering-workstream", tree!.Root[0].RelPath);
        Assert.Equal("Workstream", tree.Root[0].Title);
        Assert.Contains(tree.Root.Skip(1), n => n.RelPath == "architecture");
    }

    // ---- WriteWikiFile content lock ----

    [Fact]
    public void WriteWikiFile_RejectsFrameShells_AllowsSubpages()
    {
        var projectRoot = Path.Combine(_tempDir, "write-proj");
        SeedFrame(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, "docs", "engineering-workstream", "40-decision-log", "adr-0001.md"),
            "# ADR 1\n");

        var docs = BuildDocsService(("Write", projectRoot));

        var blockedOverview = docs.WriteWikiFile("Write", "engineering-workstream/00-overview.html", "<h1>hacked</h1>");
        Assert.False(blockedOverview.Success);
        Assert.Contains("fixed Workstream frame", blockedOverview.Error);

        var blockedShell = docs.WriteWikiFile("Write", "engineering-workstream/40-decision-log/index.html", "<h1>hacked</h1>");
        Assert.False(blockedShell.Success);

        var allowed = docs.WriteWikiFile("Write", "engineering-workstream/40-decision-log/adr-0001.md", "# ADR 1\n\nBody.\n");
        Assert.True(allowed.Success);
        Assert.True(allowed.Changed);
    }

    // ---- helpers ----

    private static void SeedFrame(string projectRoot)
    {
        var frameDir = Path.Combine(projectRoot, "docs", "engineering-workstream");
        Directory.CreateDirectory(frameDir);
        File.WriteAllText(Path.Combine(frameDir, "00-overview.html"), "<h1>Engineering Workstream</h1>");
        foreach (var area in EngineeringWorkstreamFrame.Areas)
        {
            var folder = Path.Combine(projectRoot, "docs", area.FolderRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.html"), $"<h1>{area.Title}</h1>");
        }
    }

    private static WikiTreeNode? FindNode(IEnumerable<WikiTreeNode> nodes, string relPath)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.RelPath, relPath, StringComparison.OrdinalIgnoreCase)) return n;
            var hit = FindNode(n.Children, relPath);
            if (hit != null) return hit;
        }
        return null;
    }

    private ProjectDocsService BuildDocsService(params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries),
               new ProjectRegistry(BuildConfig(entries), NullLogger<ProjectRegistry>.Instance),
               NullLogger<ProjectDocsService>.Instance);

    private static IConfiguration BuildConfig((string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TaskScannerService BuildScanner((string Name, string RootPath)[] entries)
    {
        var config = BuildConfig(entries);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }
}
