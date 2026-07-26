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
        Assert.Equal(WorkbenchProvenanceStates.ExactRevision, clean.ProvenanceState);
        Assert.StartsWith("sha256:", clean.ContentFingerprint);

        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "provenance", "index.html"),
            "<p>Uncommitted bytes</p>");
        var dirty = service.Read("Project", "provenance")!;

        Assert.Null(dirty.Revision);
        Assert.True(dirty.WorkingTreeModified);
        Assert.Equal(WorkbenchProvenanceStates.Dirty, dirty.ProvenanceState);
        Assert.NotEmpty(dirty.FreshnessFailures!);
        Assert.Contains("Uncommitted bytes", dirty.Html);
    }

    [Fact]
    public void ResolveAttachment_IsBoundedServerResolvedAndCarriesValidatedSelection()
    {
        WriteWorkbench("bounded", "Bounded", "active", "2026-07-12T10:00:00Z");
        var dir = Path.Combine(_root, "docs", "workbenches", "bounded");
        File.WriteAllText(
            Path.Combine(dir, "brief.md"),
            new string('x', WorkbenchCatalogueService.MaxAttachmentTextChars + 100) + "DO-NOT-INCLUDE");

        var attachment = Service().ResolveAttachment(
            "Project",
            new WorkbenchAttachmentRequest(
                "bounded",
                Selection: new WorkbenchPresentationSelection("variant", "compact", "Compact")));

        Assert.Equal("bounded", attachment.Id);
        Assert.Equal("docs/workbenches/bounded/workbench.json", attachment.DescriptorPath);
        Assert.Equal("docs/workbenches/bounded/index.html", attachment.EntrypointPath);
        Assert.Equal("docs/workbenches/bounded/brief.md", attachment.ContextSourcePath);
        Assert.Equal(WorkbenchCatalogueService.MaxAttachmentTextChars, attachment.ContextText.Length);
        Assert.DoesNotContain("DO-NOT-INCLUDE", attachment.ContextText);
        Assert.Equal("compact", attachment.PresentationSelection!.Value);
        Assert.Equal(WorkbenchProvenanceStates.Unavailable, attachment.ProvenanceState);
        Assert.NotEmpty(attachment.FreshnessFailures);
    }

    [Fact]
    public void ResolveAttachment_RejectsStaleRevisionAndSelection()
    {
        WriteWorkbench("fresh", "Fresh", "active", "2026-07-12T10:00:00Z");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed workbench");

        var service = Service();
        var stale = Assert.Throws<WorkbenchAttachmentException>(() =>
            service.ResolveAttachment(
                "Project",
                new WorkbenchAttachmentRequest("fresh", ExpectedRevision: new string('0', 40))));
        Assert.Equal("stale", stale.Code);

        var invalid = Assert.Throws<WorkbenchAttachmentException>(() =>
            service.ResolveAttachment(
                "Project",
                new WorkbenchAttachmentRequest(
                    "fresh",
                    Selection: new WorkbenchPresentationSelection("variant", new string('x', 257)))));
        Assert.Equal("invalid", invalid.Code);
    }

    [Fact]
    public void ResolveAttachment_OmitsCredentialLikeBriefText()
    {
        WriteWorkbench("credentials", "Credentials", "active", "2026-07-12T10:00:00Z");
        var brief = Path.Combine(
            _root, "docs", "workbenches", "credentials", "brief.md");
        File.WriteAllText(brief, "password = do-not-expose");

        var attachment = Service().ResolveAttachment(
            "Project",
            new WorkbenchAttachmentRequest("credentials"));

        Assert.DoesNotContain("do-not-expose", attachment.ContextText);
        Assert.Contains(
            attachment.ValidationFailures,
            failure => failure.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveAttachment_RejectsInvalidDescriptor()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "invalid-attachment");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Invalid</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), "{not-json");

        var error = Assert.Throws<WorkbenchAttachmentException>(() =>
            Service().ResolveAttachment(
                "Project",
                new WorkbenchAttachmentRequest("invalid-attachment")));

        Assert.Equal("not-found", error.Code);
    }

    [Fact]
    public void ResolveAttachment_CarriesExactRevisionProvenance()
    {
        WriteWorkbench("exact", "Exact", "active", "2026-07-12T10:00:00Z");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed workbench");
        var head = RunGit("rev-parse", "HEAD").Trim();

        var attachment = Service().ResolveAttachment(
            "Project",
            new WorkbenchAttachmentRequest("exact", ExpectedRevision: head));

        Assert.Equal(WorkbenchProvenanceStates.ExactRevision, attachment.ProvenanceState);
        Assert.Equal(head, attachment.Revision);
        Assert.Empty(attachment.FreshnessFailures);
    }

    [Fact]
    public void ResolveTaskReferences_EnumeratesOnceAndDoesNotLeakAnotherProject()
    {
        var enumerationCount = 0;
        var tasks = new CountingEnumerable<TaskInfo>(
            [
                new TaskInfo
                {
                    ProjectName = "Alpha",
                    Key = "ALPHA-1",
                    Id = "alpha-one",
                    TaskKey = "/secret/alpha::alpha-one",
                    Title = "Allowed title",
                    State = TaskStates.Ready,
                },
                new TaskInfo
                {
                    ProjectName = "Beta",
                    Key = "BETA-1",
                    Id = "beta-one",
                    TaskKey = "/secret/beta::beta-one",
                    Title = "Secret beta title",
                    State = TaskStates.Progress,
                },
            ],
            () => enumerationCount++);
        var failures = new List<string>();

        var references = WorkbenchCatalogueService.ResolveTaskReferences(
            "Alpha", tasks, ["ALPHA-1"], ["BETA-1"], failures);

        Assert.Equal(1, enumerationCount);
        var allowed = Assert.Single(references, reference => reference.Key == "ALPHA-1");
        Assert.Equal("resolved", allowed.Status);
        Assert.Equal("ALPHA-1", allowed.TaskKey);
        Assert.DoesNotContain("/secret/", allowed.TaskKey);
        var isolated = Assert.Single(references, reference => reference.Key == "BETA-1");
        Assert.Equal("unavailable", isolated.Status);
        Assert.Null(isolated.Title);
        Assert.DoesNotContain(references, reference => reference.Title == "Secret beta title");
    }

    [Fact]
    public void TranscriptAnchor_PersistsInCanonicalProjectTranscriptWithoutModelTurn()
    {
        WriteWorkbench("anchor", "Anchor", "active", "2026-07-12T10:00:00Z");
        var (service, scanner) = Services();
        var watchPath = Path.Combine(_root, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        var anchors = new WorkbenchTranscriptAnchorService(chat, service, scanner);
        var observed = service.Read("Project", "anchor")!;
        var entrypoint = Path.Combine(
            _root, "docs", "workbenches", "anchor", "index.html");
        var entrypointBefore = File.ReadAllText(entrypoint);
        var isolated = Assert.Throws<WorkbenchAttachmentException>(() =>
            anchors.Append(
                "Project",
                _outside,
                new WorkbenchTranscriptAnchorRequest(
                    "open",
                    new WorkbenchAttachmentRequest("anchor"))));
        Assert.Equal("invalid", isolated.Code);

        var persisted = anchors.Append(
            "Project",
            watchPath,
            new WorkbenchTranscriptAnchorRequest(
                "open",
                new WorkbenchAttachmentRequest(
                    "anchor",
                    ExpectedContentFingerprint: observed.ContentFingerprint)));

        Assert.Equal(OrchestratorChatRoles.Anchor, persisted.Role);
        Assert.Equal("anchor", persisted.WorkbenchAnchor!.WorkbenchId);
        Assert.Equal(observed.ContentFingerprint, persisted.WorkbenchAnchor.ContentFingerprint);
        Assert.True(OrchestratorContextKey.TryParse("project:Project", out var projectContext));
        var read = Assert.Single(chat.Read(watchPath, projectContext));
        Assert.Equal(persisted.WorkbenchAnchor, read.WorkbenchAnchor);
        Assert.True(OrchestratorContextKey.TryParse("task:Project/AGT-1", out var taskContext));
        Assert.Empty(chat.Read(watchPath, taskContext));
        Assert.Equal(entrypointBefore, File.ReadAllText(entrypoint));
        Assert.Empty(Directory.EnumerateFiles(watchPath, "task.json", SearchOption.AllDirectories));
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

    private WorkbenchCatalogueService Service() => Services().Service;

    private (WorkbenchCatalogueService Service, TaskScannerService Scanner) Services()
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
        return (new WorkbenchCatalogueService(scanner, registry, git), scanner);
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

    private sealed class CountingEnumerable<T>(
        IReadOnlyList<T> items,
        Action onEnumerate) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            onEnumerate();
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
