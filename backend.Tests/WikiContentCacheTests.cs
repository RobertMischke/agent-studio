using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiContentCacheTests : IDisposable
{
    private const string ProjectName = "WikiCache";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wiki-content-cache-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ProjectDocsService _docs;
    private readonly WikiContentCache _cache;

    public WikiContentCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator", "jobs"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide", "start.md"), "# Start\n\nInitial summary.\n");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _root,
                ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
                ["TaskWatcher:DebounceMs"] = "50",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _docs = new ProjectDocsService(
            _scanner,
            registry,
            NullLogger<ProjectDocsService>.Instance);
        _cache = new WikiContentCache(_docs, NullLogger<WikiContentCache>.Instance);
        _docs.SetWikiContentCache(_cache);
    }

    [Fact]
    public void Preload_FillsOnce_AndAllWikiReadersShareTheSnapshot()
    {
        Assert.True(_cache.Preload(ProjectName));
        Assert.Equal(1, _cache.Fills);

        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config);
        Assert.NotNull(_docs.GetWikiTreeResult(ProjectName));
        Assert.NotNull(_docs.GetWikiOverview(ProjectName));
        Assert.NotNull(_docs.GetWikiFolder(ProjectName, "guide", git));
        Assert.NotNull(_docs.GetWikiHome(ProjectName));
        Assert.NotNull(_docs.GetWikiRecentEditsResult(ProjectName, git));
        Assert.NotNull(_docs.GetWikiPulse(ProjectName, git));

        Assert.Equal(1, _cache.Fills);
        Assert.True(_cache.Hits >= 6);
    }

    [Fact]
    public void Mutation_EagerlyRebuilds_AndGuaranteesReadAfterWrite()
    {
        Assert.True(_cache.Preload(ProjectName));
        var before = _docs.GetWikiTreeResult(ProjectName);

        var write = _docs.WriteWikiFile(
            ProjectName,
            "guide/start.md",
            "# Changed immediately\n\nUpdated summary.\n");

        Assert.True(write.Success);
        Assert.True(write.Changed);
        Assert.Equal(2, _cache.Fills);
        Assert.Equal(1, _cache.MutationInvalidations);

        var after = _docs.GetWikiTreeResult(ProjectName);
        Assert.NotNull(after);
        Assert.NotEqual(before!.ETag, after!.ETag);
        Assert.Equal(
            "Changed immediately",
            Assert.Single(Assert.Single(after.Tree.Root).Children).Title);
        Assert.Equal("Updated summary.", Assert.Single(_docs.GetWikiFolder(ProjectName, "guide")!.Children).Summary);
        Assert.Equal(2, _cache.Fills);
    }

    [Fact]
    public async Task WatcherEvent_EagerlyRebuilds_BeforePublishingTheEvent()
    {
        Assert.True(_cache.Preload(ProjectName));
        using var watcher = new TaskWatcherService(
            _scanner,
            NullLogger<TaskWatcherService>.Instance,
            _config);
        var rebuilt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.OnWikiChanged += (project, _) =>
        {
            _cache.Invalidate(project, WikiContentCache.InvalidationSource.Watcher);
            rebuilt.TrySetResult(true);
        };

        var added = Path.Combine(_root, "docs", "guide", "external.md");
        File.WriteAllText(added, "# External\n");
        watcher.HandleWikiChange(ProjectName, added);

        Assert.True(await rebuilt.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, _cache.Fills);
        Assert.Equal(1, _cache.WatcherInvalidations);
        Assert.Contains(
            Assert.Single(_docs.GetWikiTreeResult(ProjectName)!.Tree.Root).Children,
            node => node.Name == "external.md");
        Assert.Equal(2, _cache.Fills);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
