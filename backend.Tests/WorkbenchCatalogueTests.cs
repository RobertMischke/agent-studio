using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-tests-" + Guid.NewGuid().ToString("N"));

    public WorkbenchCatalogueTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void List_ValidatesSortsAndKeepsInvalidEntriesVisible()
    {
        WriteWorkbench("older", "Older", "active", "2026-07-10T10:00:00Z");
        WriteWorkbench("newer", "Newer", "decision-pending", "2026-07-12T10:00:00Z");
        var invalid = Path.Combine(_root, "docs", "workbenches", "broken");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "workbench.json"), "{not-json");

        var catalogue = Service().List("Project")!;

        Assert.Equal(3, catalogue.Count);
        Assert.Equal(new[] { "newer", "older" }, catalogue.Items.Where(x => x.Valid).Select(x => x.Id));
        Assert.Contains(catalogue.Items, x => x.Id == "broken" && !x.Valid && x.Error != null);
    }

    [Fact]
    public void List_HidesSettledItemsUnlessHistoryRequested()
    {
        WriteWorkbench("current", "Current", "active", "2026-07-12T10:00:00Z");
        WriteWorkbench("done", "Done", "archived", "2026-07-11T10:00:00Z");
        Assert.Single(Service().List("Project")!.Items);
        Assert.Equal(2, Service().List("Project", includeHistory: true)!.Items.Count);
    }

    [Fact]
    public void Read_RejectsEscapingEntrypointAndDiscoversNamedLegacyPilot()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "escape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {"schemaVersion":1,"id":"escape","title":"Escape","summary":"Bad", "entrypoint":"../../../secret.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
          """);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "design"));
        File.WriteAllText(Path.Combine(_root, "docs", "design", "app-survey-2026-07-11.html"), "<h1>Survey</h1>");

        var service = Service();
        var catalogue = service.List("Project")!;
        Assert.Contains(catalogue.Items, x => x.Id == "escape" && !x.Valid);
        Assert.Contains(catalogue.Items, x => x.Id == "app-survey" && x.Valid);
        Assert.Null(service.Read("Project", "escape"));
        Assert.Equal("<h1>Survey</h1>", service.Read("Project", "app-survey")!.Html);
    }

    private void WriteWorkbench(string id, string title, string status, string updatedAt)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"{{title}}","summary":"Question", "entrypoint":"index.html","status":"{{status}}","phase":"testing","updatedAt":"{{updatedAt}}"}
          """);
    }

    private WorkbenchCatalogueService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new WorkbenchCatalogueService(scanner, registry, git);
    }
}
