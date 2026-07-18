using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the saved category drag-order
/// (<see cref="ProjectDocsService.SetWikiFolderOrder"/> +
/// <c>docs/.wiki-order.json</c>): persistence, the wiki tree and the folder
/// overview following the stored order (unknown folders behind,
/// alphabetically), the alphabetical fallback when no order is stored,
/// input validation, and the order file staying invisible as a page.
/// </summary>
public class WikiFolderOrderTests : IDisposable
{
    private const string ProjectName = "OrderProj";

    private readonly string _tempDir;
    private readonly string _docsDir;

    public WikiFolderOrderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-order-tests-" + Guid.NewGuid().ToString("N"));
        _docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(_docsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Fallback (no order file) ----

    [Fact]
    public void GetWikiTree_WithoutOrderFile_SortsFoldersAlphabetically()
    {
        WritePage("gamma/page.md", "# G\n");
        WritePage("alpha/page.md", "# A\n");
        WritePage("beta/page.md", "# B\n");

        var tree = BuildDocsService().GetWikiTree(ProjectName);

        Assert.NotNull(tree);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, tree!.Root.Select(n => n.Name).ToArray());
    }

    // ---- Persistence + tree order ----

    [Fact]
    public void SetWikiFolderOrder_PersistsAndTreeFollowsSavedOrder_UnknownFoldersBehindAlphabetically()
    {
        WritePage("alpha/page.md", "# A\n");
        WritePage("beta/page.md", "# B\n");
        WritePage("gamma/page.md", "# G\n");
        WritePage("delta/page.md", "# D\n");
        var docs = BuildDocsService();

        var result = docs.SetWikiFolderOrder(ProjectName, "", new[] { "gamma", "alpha" });

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(_docsDir, ".wiki-order.json")));

        var tree = docs.GetWikiTree(ProjectName);
        Assert.NotNull(tree);
        // Saved order first (gamma, alpha), unknown folders behind alphabetically.
        Assert.Equal(new[] { "gamma", "alpha", "beta", "delta" }, tree!.Root.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SetWikiFolderOrder_ForSubfolder_OrdersOnlyThatSiblingGroup()
    {
        WritePage("top/zz/page.md", "# Z\n");
        WritePage("top/aa/page.md", "# A\n");
        WritePage("other/bb/page.md", "# B\n");
        WritePage("other/cc/page.md", "# C\n");
        var docs = BuildDocsService();

        var result = docs.SetWikiFolderOrder(ProjectName, "top", new[] { "zz", "aa" });

        Assert.True(result.Success, result.Error);
        var tree = docs.GetWikiTree(ProjectName);
        var top = tree!.Root.Single(n => n.Name == "top");
        var other = tree.Root.Single(n => n.Name == "other");
        Assert.Equal(new[] { "zz", "aa" }, top.Children.Select(n => n.Name).ToArray());
        Assert.Equal(new[] { "bb", "cc" }, other.Children.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void SetWikiFolderOrder_SecondParent_PreservesExistingOrders()
    {
        WritePage("alpha/page.md", "# A\n");
        WritePage("beta/page.md", "# B\n");
        WritePage("beta/zz/page.md", "# Z\n");
        WritePage("beta/aa/page.md", "# A\n");
        var docs = BuildDocsService();

        Assert.True(docs.SetWikiFolderOrder(ProjectName, "", new[] { "beta", "alpha" }).Success);
        Assert.True(docs.SetWikiFolderOrder(ProjectName, "beta", new[] { "zz", "aa" }).Success);

        var tree = docs.GetWikiTree(ProjectName);
        Assert.Equal(new[] { "beta", "alpha" }, tree!.Root.Select(n => n.Name).ToArray());
        Assert.Equal(
            new[] { "zz", "aa" },
            tree.Root.Single(n => n.Name == "beta").Children.Where(n => n.Type == "folder").Select(n => n.Name).ToArray());
    }

    // ---- Folder overview follows the same order ----

    [Fact]
    public void GetWikiFolder_RespectsSavedOrder_PagesStayAlphabetical()
    {
        WritePage("gamma/page.md", "# G\n");
        WritePage("alpha/page.md", "# A\n");
        WritePage("beta/page.md", "# B\n");
        WritePage("b-page.md", "# BP\n");
        WritePage("a-page.md", "# AP\n");
        var docs = BuildDocsService();

        Assert.True(docs.SetWikiFolderOrder(ProjectName, "", new[] { "beta", "gamma" }).Success);

        var view = docs.GetWikiFolder(ProjectName, null);
        Assert.NotNull(view);
        Assert.Equal(
            new[] { "beta", "gamma", "alpha", "a-page.md", "b-page.md" },
            view!.Children.Select(c => c.Name).ToArray());
    }

    // ---- Validation ----

    [Fact]
    public void SetWikiFolderOrder_RejectsUnsafeParentAndNames()
    {
        WritePage("alpha/page.md", "# A\n");
        var docs = BuildDocsService();

        Assert.False(docs.SetWikiFolderOrder(ProjectName, "../outside", new[] { "alpha" }).Success);
        Assert.False(docs.SetWikiFolderOrder(ProjectName, "missing-folder", new[] { "alpha" }).Success);
        Assert.False(docs.SetWikiFolderOrder(ProjectName, "", new[] { "nested/name" }).Success);
        Assert.False(docs.SetWikiFolderOrder(ProjectName, "", new[] { ".hidden" }).Success);
        Assert.False(docs.SetWikiFolderOrder(ProjectName, "", new[] { "" }).Success);
    }

    // ---- The order file is configuration, never a page ----

    [Fact]
    public void OrderFile_NeverSurfacesAsTreeNodeOrWikiFile()
    {
        WritePage("alpha/page.md", "# A\n");
        var docs = BuildDocsService();
        Assert.True(docs.SetWikiFolderOrder(ProjectName, "", new[] { "alpha" }).Success);

        var tree = docs.GetWikiTree(ProjectName);
        Assert.DoesNotContain(tree!.Root, n => n.Name == ".wiki-order.json");

        var overview = docs.GetWikiOverview(ProjectName);
        Assert.DoesNotContain(overview!.Files, f => f.Name == ".wiki-order.json");
    }

    // ---- Corrupt order file fails open ----

    [Fact]
    public void GetWikiTree_MalformedOrderFile_FallsBackToAlphabetical()
    {
        WritePage("beta/page.md", "# B\n");
        WritePage("alpha/page.md", "# A\n");
        File.WriteAllText(Path.Combine(_docsDir, ".wiki-order.json"), "{ not json");

        var tree = BuildDocsService().GetWikiTree(ProjectName);

        Assert.Equal(new[] { "alpha", "beta" }, tree!.Root.Select(n => n.Name).ToArray());
    }

    // ---- Fixture plumbing (mirrors WikiFolderViewTests) ----

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
