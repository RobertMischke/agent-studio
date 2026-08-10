using AgentStudio.Search;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GlobalSearchRankingTests
{
    [Fact]
    public void RankItems_PutsExactThenPrefixBeforeContains()
    {
        var items = new[]
        {
            Item("notes/AGT-2034-details.md"),
            Item("AGT-2034 follow-up"),
            Item("AGT-2034"),
        };

        var ranked = GlobalSearchService.RankItems(items, "AGT-2034").Select(x => x.Title).ToList();

        Assert.Equal(new[] { "AGT-2034", "AGT-2034 follow-up", "notes/AGT-2034-details.md" }, ranked);
    }

    [Fact]
    public void ReadFiles_PinsTrackedFilesButLeavesUntrackedFilesAtTheWorkingTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"global-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init");
            File.WriteAllText(Path.Combine(root, "README-search-proof.md"), "proof");
            RunGit(root, "add", "README-search-proof.md");
            File.WriteAllText(Path.Combine(root, "untracked-search-proof.md"), "working tree");
            var revision = new string('a', 40);

            var results = GlobalSearchService.ReadFiles(root, "Fixture", "search-proof", "#fff", revision);

            Assert.Equal(2, results.Count);
            Assert.Equal(revision, Assert.Single(results, item => item.Path == "README-search-proof.md").Revision);
            Assert.Null(Assert.Single(results, item => item.Path == "untracked-search-proof.md").Revision);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static GlobalSearchItem Item(string title) =>
        new("files", "Agent Studio", "#fff", title, title);

    private static void RunGit(string root, params string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new("git") {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
