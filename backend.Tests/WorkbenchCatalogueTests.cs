using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "workbench-outside-" + Guid.NewGuid().ToString("N"));

    public WorkbenchCatalogueTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        var linkedWorkbench = Path.Combine(_root, "docs", "workbenches", "linked");
        try
        {
            if (Directory.Exists(linkedWorkbench)
                && (File.GetAttributes(linkedWorkbench) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(linkedWorkbench);
        }
        catch { }
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_outside, true); } catch { }
    }

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
    public void List_SchemaTwoUsesSharedLifecycleAsItsOnlyStoredState()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "shared-lifecycle");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Shared lifecycle</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "shared-lifecycle",
            "title": "Shared lifecycle",
            "summary": "Question",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);

        var item = Assert.Single(Service().List("Project")!.Items);

        Assert.Equal("review-requested", item.LifecycleState);
        Assert.Equal("Robert", item.EditedBy);
        Assert.Single(item.LifecycleHistory!);
        Assert.Equal("active", item.Status); // compatibility projection, not stored metadata
    }

    [Fact]
    public void List_SchemaTwoRejectsDuplicateLegacyStateAndMismatchedHistory()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "duplicate-state");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Duplicate state</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "duplicate-state",
            "title": "Duplicate state",
            "summary": "Invalid duplicate truth",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "status": "active",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "decided", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);
        var mismatchDir = Path.Combine(_root, "docs", "workbenches", "mismatched-history");
        Directory.CreateDirectory(mismatchDir);
        File.WriteAllText(Path.Combine(mismatchDir, "index.html"), "<h1>Mismatched history</h1>");
        File.WriteAllText(Path.Combine(mismatchDir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "mismatched-history",
            "title": "Mismatched history",
            "summary": "Invalid current projection",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);

        var items = Service().List("Project", includeHistory: true)!.Items;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == "duplicate-state" && !item.Valid
            && item.Error!.Contains("legacy status", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, item => item.Id == "mismatched-history" && !item.Valid
            && item.Error!.Contains("latest lifecycleHistory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LifecycleMergeKeepsSettledWorkbenchesVisibleForPulse()
    {
        WriteWorkbench("decision", "Decision", "decided", "2026-07-12T10:00:00Z");
        WriteWorkbench("complete", "Complete", "archived", "2026-07-11T10:00:00Z");
        var catalogue = Service().List("Project", includeHistory: true)!;

        var merged = ProjectDocsService.MergeWorkbenchLifecycle(
            new WikiPulseLifecycle(true, null, 0, []), catalogue);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged.Items, item => item.WorkbenchId == "decision" && item.State == "decided");
        Assert.Contains(merged.Items, item => item.WorkbenchId == "complete" && item.State == "done");
    }

    [Fact]
    public void Read_RejectsEscapingEntrypointAndDiscoversNamedLegacyPilot()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "escape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion":2,"id":"escape","title":"Escape","summary":"Bad",
            "entrypoint":"../../../secret.html","pageKind":"workbench",
            "lifecycleState":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z",
            "lifecycleHistory":[{"state":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z"}],
            "decision":null
          }
          """);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality", "design"));
        File.WriteAllText(Path.Combine(_root, "docs", "quality", "design", "app-survey-2026-07-11.html"), "<h1>Survey</h1>");

        var service = Service();
        var catalogue = service.List("Project")!;
        Assert.Contains(catalogue.Items, x => x.Id == "escape" && !x.Valid);
        Assert.Contains(catalogue.Items, x => x.Id == "app-survey" && x.Valid);
        Assert.Null(service.Read("Project", "escape"));
        Assert.Equal("<h1>Survey</h1>", service.Read("Project", "app-survey")!.Html);
    }

    [SkippableFact]
    public void List_RejectsWorkbenchDirectorySymlinkThatEscapesRepository()
    {
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "index.html"), "<h1>Outside</h1>");
        File.WriteAllText(Path.Combine(_outside, "workbench.json"), """
          {
            "schemaVersion":2,"id":"linked","title":"Linked","summary":"Bad",
            "entrypoint":"index.html","pageKind":"workbench",
            "lifecycleState":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z",
            "lifecycleHistory":[{"state":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z"}],
            "decision":null
          }
          """);
        var catalogueRoot = Path.Combine(_root, "docs", "workbenches");
        Directory.CreateDirectory(catalogueRoot);
        var link = Path.Combine(catalogueRoot, "linked");
        Skip.IfNot(TryCreateDirectoryLink(link, _outside),
            "Symbolic links and directory junctions are unavailable on this host.");

        var service = Service();
        var item = Assert.Single(service.List("Project")!.Items, candidate => candidate.Id == "linked");

        Assert.False(item.Valid);
        Assert.Contains("symbolic link", item.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(service.Read("Project", "linked"));
    }

    [Fact]
    public void List_RejectsHtmlOverTwentyMiBWithoutReadingIt()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "oversized");
        Directory.CreateDirectory(dir);
        using (var html = new FileStream(Path.Combine(dir, "index.html"), FileMode.Create, FileAccess.Write))
            html.SetLength(21L * 1024 * 1024);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion":2,"id":"oversized","title":"Oversized","summary":"Too large",
            "entrypoint":"index.html","pageKind":"workbench",
            "lifecycleState":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z",
            "lifecycleHistory":[{"state":"in-progress","editedBy":"Tests","editedAt":"2026-07-12T10:00:00Z"}],
            "decision":null
          }
          """);

        var service = Service();
        var item = Assert.Single(service.List("Project")!.Items, candidate => candidate.Id == "oversized");

        Assert.False(item.Valid);
        Assert.Contains("20 MiB", item.Error);
        Assert.Null(service.Read("Project", "oversized"));
    }

    [Fact]
    public void Read_DoesNotLabelDirtyWorkingTreeBytesAsHeadRevision()
    {
        WriteWorkbench("provenance", "Provenance", "active", "2026-07-12T10:00:00Z");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed workbench");
        var head = RunGit("rev-parse", "HEAD").Trim();

        var service = Service();
        var clean = service.Read("Project", "provenance")!;
        Assert.Equal(head, clean.Revision);
        Assert.False(clean.WorkingTreeModified);

        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "provenance", "index.html"),
            "<p>Uncommitted bytes</p>");
        var dirty = service.Read("Project", "provenance")!;

        Assert.Null(dirty.Revision);
        Assert.True(dirty.WorkingTreeModified);
        Assert.Contains("Uncommitted bytes", dirty.Html);
    }

    [Fact]
    public void CanonicalWorkbenchPathsCannotUseGenericWikiClassification()
    {
        WriteWorkbench("owned", "Owned", "active", "2026-07-12T10:00:00Z");
        var service = Service();

        Assert.True(service.OwnsCanonicalPath(
            "Project", "docs/workbenches/owned/index.html"));
        Assert.True(service.OwnsCanonicalPath(
            "Project", "workbenches/owned/workbench.json"));
        Assert.False(service.OwnsCanonicalPath(
            "Project", "docs/concepts/ordinary.md"));
    }

    private void WriteWorkbench(string id, string title, string status, string updatedAt)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        var lifecycle = status switch
        {
            "decided" => "decided",
            "archived" => "done",
            "decision-pending" => "review-requested",
            _ => "in-progress",
        };
        var decision = status switch
        {
            "decision-pending" => $$"""
              {
                "outcome": "feature-spawn",
                "state": "failed",
                "operationId": "operation-{{id}}",
                "sourceRevision": "1234567",
                "sourceFingerprint": "{{new string('a', 64)}}",
                "preparedAt": "{{updatedAt}}",
                "preparedBy": "Tests",
                "confirmedAt": "{{updatedAt}}",
                "confirmedBy": "Tests",
                "failure": "Injected failure",
                "taskDraft": {
                  "title": "Feature",
                  "goal": "Implement it.",
                  "acceptanceCriteria": ["It works."],
                  "evidenceLinks": [],
                  "relatedTaskKeys": [],
                  "initialLane": "1-preparation",
                  "mode": "coding",
                  "taskType": "feature"
                },
                "spawnedTaskKeys": []
              }
              """,
            "decided" => $$"""
              {
                "outcome": "feature-spawn",
                "state": "succeeded",
                "operationId": "operation-{{id}}",
                "sourceRevision": "1234567",
                "sourceFingerprint": "{{new string('b', 64)}}",
                "preparedAt": "{{updatedAt}}",
                "preparedBy": "Tests",
                "confirmedAt": "{{updatedAt}}",
                "confirmedBy": "Tests",
                "decidedAt": "{{updatedAt}}",
                "taskDraft": {
                  "title": "Feature",
                  "goal": "Implement it.",
                  "acceptanceCriteria": ["It works."],
                  "evidenceLinks": [],
                  "relatedTaskKeys": [],
                  "initialLane": "1-preparation",
                  "mode": "coding",
                  "taskType": "feature"
                },
                "spawnedTaskKeys": ["TST-1"]
              }
              """,
            "archived" => $$"""
              {
                "outcome": "archive",
                "state": "succeeded",
                "operationId": "operation-{{id}}",
                "sourceRevision": "1234567",
                "sourceFingerprint": "{{new string('c', 64)}}",
                "preparedAt": "{{updatedAt}}",
                "preparedBy": "Tests",
                "confirmedAt": "{{updatedAt}}",
                "confirmedBy": "Tests",
                "decidedAt": "{{updatedAt}}",
                "reason": "Experiment no longer justifies implementation.",
                "spawnedTaskKeys": []
              }
              """,
            _ => "null",
        };
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "{{title}}",
            "summary": "Question",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "{{lifecycle}}",
            "phase": "testing",
            "editedBy": "Tests",
            "editedAt": "{{updatedAt}}",
            "lifecycleHistory": [
              { "state": "{{lifecycle}}", "editedBy": "Tests", "editedAt": "{{updatedAt}}" }
            ],
            "decision": {{decision}}
          }
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

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
