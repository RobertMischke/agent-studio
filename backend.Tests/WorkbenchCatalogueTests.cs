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
        WriteWorkbench("tracking", "Tracking", "decided", "2026-07-11T11:00:00Z");
        WriteWorkbench("documented", "Documented", "documented", "2026-07-11T10:30:00Z");
        WriteWorkbench("done", "Done", "archived", "2026-07-11T10:00:00Z");
        Assert.Equal(new[] { "current", "tracking" }, Service().List("Project")!.Items.Select(item => item.Id));
        Assert.Equal(4, Service().List("Project", includeHistory: true)!.Items.Count);
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
    public void List_ProjectsDurableDecisionReceipt()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "settled-decision");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Settled decision</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "settled-decision",
            "title": "Settled decision",
            "summary": "Durable receipt",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-26T10:02:00Z",
            "lifecycleHistory": [
              { "state": "decided", "editedBy": "Robert", "editedAt": "2026-07-26T10:02:00Z" }
            ],
            "decision": {
              "outcome": "feature-spawn",
              "state": "succeeded",
              "operationId": "workbench-ui-settled",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "sourceFingerprint": null,
              "preparedAt": "2026-07-26T10:00:00Z",
              "preparedBy": "Robert",
              "confirmedAt": "2026-07-26T10:01:00Z",
              "confirmedBy": "Robert",
              "decidedAt": "2026-07-26T10:02:00Z",
              "spawnedTaskKeys": ["AGT-2400"],
              "taskDraft": {
                "title": "Implement the decision",
                "goal": "Ship the confirmed Workbench direction.",
                "acceptanceCriteria": ["The direction is implemented and verified."],
                "evidenceLinks": [],
                "relatedTaskKeys": [],
                "initialLane": "1-preparation",
                "mode": "coding",
                "taskType": "feature"
              }
            }
          }
          """);

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.Equal("decided", item.Status);
        Assert.Equal("succeeded", item.DecisionStage);
        Assert.Equal("feature-spawn", item.Decision!.Outcome);
        Assert.Equal(new[] { "AGT-2400" }, item.Decision.SpawnedTaskKeys);
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
    public void List_ProjectsALegacyDecisionReceiptWithoutCouplingItToALifecycleState()
    {
        // Schema v1 has no lifecycleState, but it stores the receipt under the
        // same key. Without this projection the decision service cannot see the
        // operationId it wrote and answers a retry with 409 instead of the
        // settled result (AGT-2375).
        var dir = Path.Combine(_root, "docs", "workbenches", "legacy-settled");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Legacy settled</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 1,
            "id": "legacy-settled",
            "title": "Legacy settled",
            "summary": "Question",
            "entrypoint": "index.html",
            "status": "archived",
            "updatedAt": "2026-07-26T10:02:00Z",
            "decision": {
              "outcome": "archive",
              "state": "succeeded",
              "operationId": "workbench-ui-legacy",
              "sourceFingerprint": "{{new string('a', 64)}}",
              "preparedAt": "2026-07-26T10:00:00Z",
              "preparedBy": "Robert",
              "confirmedAt": "2026-07-26T10:01:00Z",
              "confirmedBy": "Robert",
              "decidedAt": "2026-07-26T10:02:00Z",
              "reason": "The experiment disproved the direction.",
              "spawnedTaskKeys": []
            }
          }
          """);

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.True(item.Valid, item.Error);
        Assert.Equal("archived", item.Status); // still the flat v1 field, not a lifecycle projection
        Assert.Equal("archived", item.DecisionStage);
        Assert.Equal("workbench-ui-legacy", item.Decision!.OperationId);
    }

    [Fact]
    public void OwnsCanonicalPath_CoversBothDescriptorSchemas()
    {
        // The Wiki-classification archive bug is a property of workbench.json,
        // not of its version: the repository's Workbenches are overwhelmingly
        // schema v1, so a v2-only guard protected almost none of them.
        WriteWorkbench("legacy", "Legacy", "active", "2026-07-12T10:00:00Z");
        WriteSchemaTwoWorkbench("modern");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "concepts"));
        File.WriteAllText(Path.Combine(_root, "docs", "concepts", "plain.md"), "# Plain\n");

        var service = Service();

        Assert.True(service.OwnsCanonicalPath("Project", "docs/workbenches/legacy/index.html"));
        Assert.True(service.OwnsCanonicalPath("Project", "workbenches/legacy/notes.md"));
        Assert.True(service.OwnsCanonicalPath("Project", "docs/workbenches/modern/index.html"));
        Assert.False(service.OwnsCanonicalPath("Project", "docs/concepts/plain.md"));
    }

    [Fact]
    public void LifecycleMergeKeepsSettledWorkbenchesVisibleForPulse()
    {
        WriteWorkbench("decision", "Decision", "decided", "2026-07-12T10:00:00Z");
        WriteWorkbench("documented", "Documented", "documented", "2026-07-11T11:00:00Z");
        WriteWorkbench("complete", "Complete", "archived", "2026-07-11T10:00:00Z");
        var catalogue = Service().List("Project", includeHistory: true)!;

        var merged = ProjectDocsService.MergeWorkbenchLifecycle(
            new WikiPulseLifecycle(true, null, 0, []), catalogue);

        Assert.Equal(3, merged.Count);
        Assert.Contains(merged.Items, item => item.WorkbenchId == "decision" && item.State == "decided");
        Assert.Contains(merged.Items, item => item.WorkbenchId == "documented" && item.State == "documented");
        Assert.Contains(merged.Items, item => item.WorkbenchId == "complete" && item.State == "done");
    }

    [Fact]
    public void Read_RejectsEscapingEntrypointAndDiscoversNamedLegacyPilot()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "escape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {"schemaVersion":1,"id":"escape","title":"Escape","summary":"Bad", "entrypoint":"../../../secret.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
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
          {"schemaVersion":1,"id":"linked","title":"Linked","summary":"Bad", "entrypoint":"index.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
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
          {"schemaVersion":1,"id":"oversized","title":"Oversized","summary":"Too large", "entrypoint":"index.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
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
        Assert.NotNull(clean.Fingerprint);

        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "provenance", "index.html"),
            "<p>Uncommitted bytes</p>");
        var dirty = service.Read("Project", "provenance")!;

        Assert.Null(dirty.Revision);
        Assert.True(dirty.WorkingTreeModified);
        Assert.NotEqual(clean.Fingerprint, dirty.Fingerprint);
        Assert.Contains("Uncommitted bytes", dirty.Html);
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

    private void WriteSchemaTwoWorkbench(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "Modern",
            "summary": "Question",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-26T10:00:00Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-26T10:00:00Z" }
            ]
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
