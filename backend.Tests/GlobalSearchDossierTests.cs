using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Dossier domain of the global search: a document reference key such as
/// <c>AGT-W15</c> must reach its Dossier viewer, settled Dossiers stay
/// findable, and a hidden project contributes nothing.
/// </summary>
public sealed class GlobalSearchDossierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "global-search-dossiers-" + Guid.NewGuid().ToString("N"));
    private readonly string _retiredRoot = Path.Combine(
        Path.GetTempPath(), "global-search-retired-" + Guid.NewGuid().ToString("N"));

    public GlobalSearchDossierTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_retiredRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_retiredRoot, true); } catch { }
    }

    [Fact]
    public void Search_RanksAnExactDocumentKeyBeforeTitleAndSummaryMatches()
    {
        WriteDossier(_root, "operations", "orchestrator-waechter", "AGT-W15",
            "Watcher", "Autonomous problem finding.", "active", "2026-09-01T10:00:00Z");
        WriteDossier(_root, "operations", "waechter-followup", "AGT-W16",
            "AGT-W15 follow-up", "Later slices.", "active", "2026-09-02T10:00:00Z");
        WriteDossier(_root, "quality", "waechter-notes", "AGT-W17",
            "Notes", "Depends on AGT-W15 for the ticket proposals.", "active", "2026-09-03T10:00:00Z");

        var results = Search("AGT-W15");

        Assert.Equal(
            new[] { "orchestrator-waechter", "waechter-followup", "waechter-notes" },
            results.Select(item => item.DossierId));
        Assert.All(results, item => Assert.Equal("dossiers", item.Domain));
        Assert.All(results, item => Assert.Null(item.Path));
    }

    [Fact]
    public void Search_MatchesTitleWordsAndCarriesTheViewerRoute()
    {
        WriteDossier(_root, "operations", "runner-link", "AGT-W20",
            "Runner link", "How a runner attaches to a task.", "decision-pending", "2026-09-01T10:00:00Z");

        var item = Assert.Single(Search("runner link"));

        Assert.Equal("Runner link", item.Title);
        Assert.Equal("Studio", item.ProjectName);
        Assert.Equal("runner-link", item.DossierId);
        Assert.Equal("AGT-W20", item.DossierKey);
        Assert.Equal("decision-pending · testing", item.Subtitle);
        Assert.Equal("How a runner attaches to a task.", item.Summary);
    }

    [Fact]
    public void Search_IncludesSettledDossiersSoAKeyStaysReachable()
    {
        WriteDossier(_root, "operations", "log-retention", "AGT-W11",
            "Log retention", "Superseded by retention and archive.", "archived", "2026-08-01T10:00:00Z");

        var item = Assert.Single(Search("AGT-W11"));

        Assert.Equal("log-retention", item.DossierId);
        Assert.Equal("archived · testing", item.Subtitle);
    }

    [Fact]
    public void Search_ExcludesDossiersOfAnArchivedProject()
    {
        WriteDossier(_root, "operations", "live-dossier", "AGT-W30",
            "Retention", "Kept visible.", "active", "2026-09-01T10:00:00Z");
        WriteDossier(_retiredRoot, "operations", "retired-dossier", "RET-W1",
            "Retention", "Hidden with its project.", "active", "2026-09-02T10:00:00Z");

        var results = Search("Retention", archiveRetiredProject: true);

        Assert.Equal(new[] { "live-dossier" }, results.Select(item => item.DossierId));
    }

    private IReadOnlyList<GlobalSearchItem> Search(string query, bool archiveRetiredProject = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Studio",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
            ["WatchPaths:1:Name"] = "Retired",
            ["WatchPaths:1:RootPath"] = _retiredRoot,
            ["WatchPaths:1:Path"] = Path.Combine(_retiredRoot, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(
            Path.Combine(_root, ".orchestrator", "jobs"), "Studio", DefaultWorkspace.Id);
        var retired = registry.EnsureProjectForStorage(
            Path.Combine(_retiredRoot, ".orchestrator", "jobs"), "Retired", DefaultWorkspace.Id);
        if (archiveRetiredProject) registry.SetArchived(retired.Id, true);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var workbenches = new WorkbenchCatalogueService(scanner, registry, git);
        var docs = new ProjectDocsService(
            scanner, registry, NullLogger<ProjectDocsService>.Instance, git, workbenches);
        var service = new GlobalSearchService(
            scanner, git, registry, docs, NullLogger<GlobalSearchService>.Instance);

        return service.Search(query, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dossiers" }, 20)
            .Dossiers;
    }

    private static void WriteDossier(
        string root, string theme, string id, string key,
        string title, string summary, string status, string updatedAt)
    {
        var dir = Path.Combine(root, "docs", theme, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","key":"{{key}}","title":"{{title}}","summary":"{{summary}}",
           "entrypoint":"index.html","status":"{{status}}","phase":"testing","updatedAt":"{{updatedAt}}"}
          """);
    }
}
