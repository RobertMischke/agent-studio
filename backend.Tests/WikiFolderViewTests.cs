using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki folder-overview surface
/// (<see cref="ProjectDocsService.GetWikiFolder"/>): one directory level with
/// folders-first alphabetical ordering, sniffed page titles (H1 / frontmatter /
/// &lt;title&gt; / file-name fallback), markup-stripped 240-char summaries,
/// non-recursive folder child counts, and the standard traversal guard.
/// </summary>
public class WikiFolderViewTests : IDisposable
{
    private const string ProjectName = "FolderProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiFolderViewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-folder-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Listing + sorting ----

    [Fact]
    public void GetWikiFolder_Root_ListsFoldersFirstThenPagesAlphabetically()
    {
        WritePage("zeta/inner.md", "# Inner\n");
        WritePage("alpha/other.md", "# Other\n");
        WritePage("b-page.md", "# B\n");
        WritePage("a-page.md", "# A\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, null);

        Assert.NotNull(view);
        Assert.Equal("", view!.Path);
        Assert.Equal("docs", view.Name);
        Assert.Equal(
            new[] { "alpha", "zeta", "a-page.md", "b-page.md" },
            view.Children.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "folder", "folder", "page", "page" }, view.Children.Select(c => c.Kind).ToArray());
        Assert.Equal(new string?[] { null, null, "md", "md" }, view.Children.Select(c => c.FileType).ToArray());
    }

    [Fact]
    public void GetWikiFolder_Subfolder_ListsItsChildrenWithRelPaths()
    {
        WritePage("guide/sub/deep.md", "# Deep\n");
        WritePage("guide/intro.md", "# Intro\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "guide");

        Assert.NotNull(view);
        Assert.Equal("guide", view!.Path);
        Assert.Equal("guide", view.Name);
        Assert.Collection(view.Children,
            c => { Assert.Equal("sub", c.Name); Assert.Equal("guide/sub", c.RelPath); Assert.Equal("folder", c.Kind); },
            c => { Assert.Equal("intro.md", c.Name); Assert.Equal("guide/intro.md", c.RelPath); Assert.Equal("page", c.Kind); });
    }

    // ---- Titles ----

    [Fact]
    public void GetWikiFolder_ExtractsTitlesFromH1FrontmatterHtmlTitleAndFileName()
    {
        WritePage("h1.md", "---\ntitle: Ignored When H1 Present\n---\n\n# Heading Title\n\nBody.\n");
        WritePage("frontmatter.md", "---\ntitle: Frontmatter Title\nmodel: x\n---\n\nNo heading here.\n");
        WritePage("10-fallback-name.md", "Just prose, no heading.\n");
        WritePage("titled.html", "<html><head><title>Html Title</title></head><body><p>Hi</p></body></html>");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        string TitleOf(string name) => view!.Children.Single(c => c.Name == name).Title;
        Assert.Equal("Heading Title", TitleOf("h1.md"));
        Assert.Equal("Frontmatter Title", TitleOf("frontmatter.md"));
        Assert.Equal("fallback-name", TitleOf("10-fallback-name.md")); // order prefix stripped
        Assert.Equal("Html Title", TitleOf("titled.html"));
    }

    // ---- Summaries ----

    [Fact]
    public void GetWikiFolder_SummaryIsFirstParagraphStrippedAndCapped()
    {
        WritePage("prose.md",
            "---\ntitle: T\n---\n\n# Prose\n\nThis has **bold**, a [link](https://x.example), and `code` inline.\nSecond line of the same paragraph.\n\nNext paragraph is not included.\n");
        WritePage("long.md", "# Long\n\n" + string.Join(" ", Enumerable.Repeat("wordy", 100)) + "\n");
        WritePage("page.html",
            "<html><head><title>H</title><style>p{color:red}</style></head><body><h1>H</h1><p>First <b>html</b> paragraph.</p><p>Second.</p></body></html>");
        WritePage("folder/inner.md", "# Inner\n");

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        var prose = view!.Children.Single(c => c.Name == "prose.md");
        Assert.Equal("This has bold, a link, and code inline. Second line of the same paragraph.", prose.Summary);

        var longPage = view.Children.Single(c => c.Name == "long.md");
        Assert.NotNull(longPage.Summary);
        Assert.True(longPage.Summary!.Length <= 240, $"summary length {longPage.Summary.Length} > 240");

        var html = view.Children.Single(c => c.Name == "page.html");
        Assert.Equal("First html paragraph.", html.Summary);

        var folder = view.Children.Single(c => c.Name == "folder");
        Assert.Null(folder.Summary);
    }

    // ---- Folder metadata ----

    [Fact]
    public void GetWikiFolder_ChildCountIsDirectPagesPlusNonEmptySubfolders()
    {
        WritePage("guide/one.md", "# One\n");
        WritePage("guide/two.html", "<title>Two</title>");
        WritePage("guide/sub/deep.md", "# Deep\n");
        WritePage("guide/sub/deeper/leaf.md", "# Leaf\n"); // not counted (recursive)
        Directory.CreateDirectory(Path.Combine(_docsDir, "guide", "empty")); // no pages -> not counted

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        var guide = view!.Children.Single(c => c.Name == "guide");
        Assert.Equal("folder", guide.Kind);
        Assert.Equal(3, guide.ChildCount); // one.md + two.html + sub
        Assert.Null(guide.Size);

        var page = BuildDocsService().GetWikiFolder(ProjectName, "guide")!
            .Children.Single(c => c.Name == "one.md");
        Assert.Null(page.ChildCount);
        Assert.True(page.Size > 0);
        Assert.True(page.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void GetWikiFolder_SkipsCompanionsJsonHiddenAndEmptyFolders()
    {
        WritePage("real.md", "# Real\n");
        WritePage("real.md.meta.json", "{}");
        WritePage("real.md.report.html", "<html></html>");
        WritePage("data.json", "{ \"title\": \"Data\" }");
        WritePage("home.json", "{ \"sections\": [] }"); // wiki home config, never a page
        WritePage(".hidden/secret.md", "# Secret\n");
        Directory.CreateDirectory(Path.Combine(_docsDir, "empty"));

        var view = BuildDocsService().GetWikiFolder(ProjectName, "");

        Assert.Equal(new[] { "real.md" }, view!.Children.Select(c => c.Name).ToArray());
    }

    // ---- Guards ----

    [Fact]
    public void GetWikiFolder_RejectsTraversalRootedAndMissingPaths()
    {
        WritePage("guide/intro.md", "# Intro\n");
        File.WriteAllText(Path.Combine(_tempDir, "outside.md"), "# Outside\n");
        var docs = BuildDocsService();

        Assert.Null(docs.GetWikiFolder(ProjectName, "../"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "guide/../../"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "C:/temp"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "does-not-exist"));
        Assert.Null(docs.GetWikiFolder(ProjectName, "guide/intro.md")); // a file, not a folder
        Assert.Null(docs.GetWikiFolder("NopeProject", ""));
    }

    // ---- helpers ----

    private void WritePage(string relPath, string content)
    {
        var full = Path.Combine(_docsDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ProjectDocsService BuildDocsService()
    {
        var config = BuildConfig();
        return new ProjectDocsService(
            BuildScanner(config),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<ProjectDocsService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:RootPath"] = _tempDir,
            ["WatchPaths:0:Path"] = Path.Combine(_tempDir, ".orchestrator", "jobs"),
        })
        .Build();

    private static TaskScannerService BuildScanner(IConfiguration config)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }
}
