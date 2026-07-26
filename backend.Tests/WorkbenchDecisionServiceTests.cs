using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchDecisionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "workbench-decision-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly GitService _git;

    public WorkbenchDecisionServiceTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Project",
                ["WatchPaths:0:RootPath"] = _root,
                ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
            }).Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(
            config, NullLogger<ProjectRegistry>.Instance);
        _git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        _catalogue = new WorkbenchCatalogueService(scanner, registry, _git);
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Decision Tests");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void ArchiveRequiresReasonAndExplicitConfirmationThenMovesOnlyToHistory()
    {
        WriteWorkbench("archive-me");
        CommitAll("seed");
        var tasks = new FakeTasks();
        var service = Service(tasks);
        var source = _catalogue.Read("Project", "archive-me")!;

        var withoutReason = service.Prepare("Project", "archive-me",
            ArchivePrepare(source, reason: ""));
        Assert.False(withoutReason.Success);
        Assert.Equal("validation", withoutReason.ErrorCode);

        var prepared = service.Prepare("Project", "archive-me",
            ArchivePrepare(source, "The experiment disproved its premise."));
        Assert.True(prepared.Success);
        Assert.Equal("prepared", prepared.DecisionStage);
        Assert.Equal("decision-pending",
            Assert.Single(_catalogue.List("Project")!.Items).Status);

        var unconfirmed = service.Confirm("Project", "archive-me",
            Confirm(prepared, confirmed: false));
        Assert.False(unconfirmed.Success);
        Assert.Equal("validation", unconfirmed.ErrorCode);

        var archived = service.Confirm("Project", "archive-me", Confirm(prepared));
        Assert.True(archived.Success);
        Assert.Equal("archived", archived.DecisionStage);
        Assert.Empty(_catalogue.List("Project")!.Items);
        var history = Assert.Single(_catalogue.List("Project", includeHistory: true)!.Items);
        Assert.Equal("archived", history.Status);
        Assert.Equal("archive", history.Decision!.Outcome);
        Assert.Equal("The experiment disproved its premise.", history.Decision.Reason);
        Assert.Equal("done", history.LifecycleState);
        Assert.Contains(history.LifecycleHistory!,
            entry => entry.Note?.Contains("Archived by decision", StringComparison.Ordinal) == true);

        using var descriptor = ReadDescriptor("archive-me");
        var decision = descriptor.RootElement.GetProperty("decision");
        Assert.Equal("operation-archive-me", decision.GetProperty("operationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("sourceRevision").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("sourceFingerprint").GetString()));
        Assert.Equal("succeeded", decision.GetProperty("state").GetString());
        Assert.Equal("archive", decision.GetProperty("outcome").GetString());
        Assert.Empty(decision.GetProperty("spawnedTaskKeys").EnumerateArray());
        Assert.True(RunGit("status", "--porcelain").Length == 0);
    }

    [Fact]
    public void PreparationRejectsStaleDirtyLegacyAndOperationConflicts()
    {
        WriteWorkbench("first");
        WriteWorkbench("second");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality", "design"));
        File.WriteAllText(
            Path.Combine(_root, "docs", "quality", "design", "app-survey-2026-07-11.html"),
            "<h1>Legacy</h1>");
        CommitAll("seed");
        var service = Service(new FakeTasks());
        var first = _catalogue.Read("Project", "first")!;

        var stale = service.Prepare("Project", "first",
            FeaturePrepare(first) with { ExpectedRevision = "deadbee", ExpectedFingerprint = null });
        Assert.False(stale.Success);
        Assert.Equal("stale-revision", stale.ErrorCode);

        File.AppendAllText(
            Path.Combine(_root, "docs", "workbenches", "first", "workbench.json"), " ");
        var dirty = service.Prepare("Project", "first", FeaturePrepare(first));
        Assert.False(dirty.Success);
        Assert.Equal("dirty-descriptor", dirty.ErrorCode);
        RunGit("restore", "--", "docs/workbenches/first/workbench.json");

        var prepared = service.Prepare("Project", "first", FeaturePrepare(first));
        Assert.True(prepared.Success);
        var second = _catalogue.Read("Project", "second")!;
        var conflict = service.Prepare("Project", "second",
            FeaturePrepare(second) with { OperationId = "operation-feature" });
        Assert.False(conflict.Success);
        Assert.Equal("operation-id-conflict", conflict.ErrorCode);

        var legacy = service.Prepare("Project", "app-survey",
            FeaturePrepare(first) with { OperationId = "operation-legacy" });
        Assert.False(legacy.Success);
        Assert.Equal("not-canonical", legacy.ErrorCode);
    }

    [Fact]
    public void ConfirmationRejectsACommittedSourceChangeAfterPreparation()
    {
        WriteWorkbench("stale-confirm");
        CommitAll("seed");
        var tasks = new FakeTasks();
        var service = Service(tasks);
        var prepared = service.Prepare(
            "Project", "stale-confirm",
            FeaturePrepare(_catalogue.Read("Project", "stale-confirm")!));
        Assert.True(prepared.Success);
        File.AppendAllText(
            Path.Combine(_root, "docs", "workbenches", "stale-confirm", "index.html"),
            "<p>Changed after preview.</p>");
        CommitAll("change experiment after preparation");

        var stale = service.Confirm("Project", "stale-confirm", Confirm(prepared));

        Assert.False(stale.Success);
        Assert.Equal("stale-revision", stale.ErrorCode);
        Assert.Equal(0, tasks.CreatedCount);
    }

    [Fact]
    public void FailedTaskMutationRemainsCurrentAndRetrySettlesWithoutDuplicate()
    {
        WriteWorkbench("feature");
        CommitAll("seed");
        var tasks = new FakeTasks { FailNext = true };
        var service = Service(tasks);
        var prepared = service.Prepare(
            "Project", "feature", FeaturePrepare(_catalogue.Read("Project", "feature")!));
        Assert.True(prepared.Success);

        var failed = service.Confirm("Project", "feature", Confirm(prepared));
        Assert.False(failed.Success);
        Assert.Equal("task-mutation-failed", failed.ErrorCode);
        var current = Assert.Single(_catalogue.List("Project")!.Items);
        Assert.Equal("decision-pending", current.Status);
        Assert.Equal("failed", current.DecisionStage);
        Assert.Equal("failed", current.Decision!.State);

        var succeeded = service.Confirm("Project", "feature", Confirm(prepared));
        Assert.True(succeeded.Success);
        Assert.Equal("succeeded", succeeded.DecisionStage);
        Assert.Equal(["TST-1"], succeeded.SpawnedTaskKeys);
        Assert.Empty(_catalogue.List("Project")!.Items);
        var settled = Assert.Single(
            _catalogue.List("Project", includeHistory: true)!.Items);
        Assert.Equal("decided", settled.Status);
        Assert.Equal("succeeded", settled.Decision!.State);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.Contains(settled.LifecycleHistory!,
            entry => entry.Note?.Contains("failed:", StringComparison.Ordinal) == true);
        Assert.Contains(settled.LifecycleHistory!,
            entry => entry.Note?.Contains("retry started", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConcurrentConfirmationsCreateOneTaskAndReturnOneReceipt()
    {
        WriteWorkbench("concurrent");
        CommitAll("seed");
        var tasks = new FakeTasks { DelayMs = 75 };
        var service = Service(tasks);
        var prepared = service.Prepare(
            "Project", "concurrent",
            FeaturePrepare(_catalogue.Read("Project", "concurrent")!));
        var confirmation = Confirm(prepared);

        var results = await Task.WhenAll(
            Task.Run(() => service.Confirm("Project", "concurrent", confirmation)),
            Task.Run(() => service.Confirm("Project", "concurrent", confirmation)));

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Contains(results, result => result.Idempotent);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.All(results, result => Assert.Equal(["TST-1"], result.SpawnedTaskKeys));
    }

    [Fact]
    public void DescriptorWriteFailureAfterTaskCreationIsRetryableAndIdempotent()
    {
        WriteWorkbench("write-failure");
        CommitAll("seed");
        var tasks = new FakeTasks();
        var repository = new InjectedFailureRepository(
            new WorkbenchDecisionRepository(_git)) { FailWriteNumber = 3 };
        var service = Service(tasks, repository);
        var prepared = service.Prepare(
            "Project", "write-failure",
            FeaturePrepare(_catalogue.Read("Project", "write-failure")!));

        var failed = service.Confirm("Project", "write-failure", Confirm(prepared));
        Assert.False(failed.Success);
        Assert.Equal("descriptor-write-failed", failed.ErrorCode);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.Equal("pending",
            Assert.Single(_catalogue.List("Project")!.Items).DecisionStage);

        repository.FailWriteNumber = null;
        var repaired = service.Confirm("Project", "write-failure", Confirm(prepared));
        Assert.True(repaired.Success);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.Equal(["TST-1"], repaired.SpawnedTaskKeys);
    }

    [Fact]
    public void FinalCommitFailureCannotReportSuccessAndRetryOnlyRepairsDescriptor()
    {
        WriteWorkbench("commit-failure");
        CommitAll("seed");
        var tasks = new FakeTasks();
        var repository = new InjectedFailureRepository(
            new WorkbenchDecisionRepository(_git)) { FailCommitNumber = 3 };
        var service = Service(tasks, repository);
        var prepared = service.Prepare(
            "Project", "commit-failure",
            FeaturePrepare(_catalogue.Read("Project", "commit-failure")!));

        var failed = service.Confirm("Project", "commit-failure", Confirm(prepared));
        Assert.False(failed.Success);
        Assert.Equal("commit-failed", failed.ErrorCode);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.NotEmpty(RunGit("status", "--porcelain"));

        repository.FailCommitNumber = null;
        var repaired = service.Confirm("Project", "commit-failure", Confirm(prepared));
        Assert.True(repaired.Success);
        Assert.True(repaired.Idempotent);
        Assert.Equal(1, tasks.Calls);
        Assert.Equal(1, tasks.CreatedCount);
        Assert.Empty(RunGit("status", "--porcelain"));
    }

    [Fact]
    public void CatalogueRejectsSchemaOneAndMalformedDecision()
    {
        var legacyDir = Path.Combine(_root, "docs", "workbenches", "schema-one");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "index.html"), "<h1>Old</h1>");
        File.WriteAllText(Path.Combine(legacyDir, "workbench.json"), """
          {"schemaVersion":1,"id":"schema-one","title":"Old","summary":"Old",
           "entrypoint":"index.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
          """);
        WriteWorkbench("malformed");
        var malformedPath = Path.Combine(
            _root, "docs", "workbenches", "malformed", "workbench.json");
        var text = File.ReadAllText(malformedPath).Replace(
            "\"decision\": null",
            "\"decision\": {\"outcome\":\"archive\",\"state\":\"succeeded\"}");
        File.WriteAllText(malformedPath, text);

        var items = _catalogue.List("Project", includeHistory: true)!.Items;

        Assert.Contains(items, item => item.Id == "schema-one" && !item.Valid
            && item.Error!.Contains("schemaVersion must be 2", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Id == "malformed" && !item.Valid
            && item.Error!.Contains("operationId", StringComparison.Ordinal));
    }

    private WorkbenchDecisionService Service(
        FakeTasks tasks, IWorkbenchDecisionRepository? repository = null) =>
        new(
            _catalogue,
            repository ?? new WorkbenchDecisionRepository(_git),
            tasks,
            _git,
            NullLogger<WorkbenchDecisionService>.Instance);

    private static PrepareWorkbenchDecisionRequest ArchivePrepare(
        WorkbenchDocument source, string reason) =>
        new()
        {
            OperationId = "operation-archive-me",
            Outcome = "archive",
            ExpectedRevision = source.Revision,
            ExpectedFingerprint = source.Fingerprint,
            Actor = "Operator",
            ArchiveReason = reason,
        };

    private static PrepareWorkbenchDecisionRequest FeaturePrepare(WorkbenchDocument source) =>
        new()
        {
            OperationId = "operation-feature",
            Outcome = "feature-spawn",
            ExpectedRevision = source.Revision,
            ExpectedFingerprint = source.Fingerprint,
            Actor = "Operator",
            Task = new WorkbenchTaskDraft
            {
                Title = "Implement the chosen feature",
                Goal = "Turn the verified Workbench option into product behavior.",
                AcceptanceCriteria = ["The behavior is covered by tests."],
                EvidenceLinks = ["docs/workbenches/feature/index.html"],
            },
        };

    private static ConfirmWorkbenchDecisionRequest Confirm(
        WorkbenchDecisionResult prepared, bool confirmed = true) =>
        new()
        {
            OperationId = prepared.OperationId,
            ExpectedRevision = prepared.Revision,
            ExpectedFingerprint = prepared.Fingerprint,
            Actor = "Operator",
            Confirmed = confirmed,
        };

    private void WriteWorkbench(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "{{id}}",
            "summary": "A bounded experiment.",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Author",
            "editedAt": "2026-07-26T10:00:00Z",
            "lifecycleHistory": [
              {
                "state": "review-requested",
                "editedBy": "Author",
                "editedAt": "2026-07-26T10:00:00Z"
              }
            ],
            "sourceTaskKeys": ["TST-PLAN"],
            "relatedTaskKeys": ["TST-RELATED"],
            "projectUrlIds": ["preview"],
            "decision": null
          }
          """);
    }

    private JsonDocument ReadDescriptor(string id) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            _root, "docs", "workbenches", id, "workbench.json")));

    private void CommitAll(string message)
    {
        RunGit("add", ".");
        RunGit("commit", "-m", message);
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
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private sealed class FakeTasks : IWorkbenchDecisionTaskMutation
    {
        private readonly Dictionary<string, WorkbenchTaskReceipt> _receipts = [];
        public bool FailNext { get; set; }
        public int DelayMs { get; set; }
        public int Calls { get; private set; }
        public int CreatedCount => _receipts.Count;

        public WorkbenchTaskReceipt CreateOrFind(
            string navigationProject,
            string workbenchId,
            string operationId,
            WorkbenchTaskDraft draft,
            IReadOnlyList<string> sourceTaskKeys)
        {
            Calls++;
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected task mutation failure.");
            }
            if (_receipts.TryGetValue(operationId, out var receipt)) return receipt;
            receipt = new("feature-task", $"TST-{_receipts.Count + 1}");
            _receipts[operationId] = receipt;
            return receipt;
        }
    }

    private sealed class InjectedFailureRepository : IWorkbenchDecisionRepository
    {
        private readonly IWorkbenchDecisionRepository _inner;
        private int _writes;
        private int _commits;
        public int? FailWriteNumber { get; set; }
        public int? FailCommitNumber { get; set; }

        public InjectedFailureRepository(IWorkbenchDecisionRepository inner) =>
            _inner = inner;

        public void WriteDescriptorDurably(string descriptorPath, string content)
        {
            _writes++;
            if (_writes == FailWriteNumber)
                throw new IOException("Injected descriptor write failure.");
            _inner.WriteDescriptorDurably(descriptorPath, content);
        }

        public GitCommitResult CommitDescriptor(
            string root, string descriptorRelPath, string message)
        {
            _commits++;
            return _commits == FailCommitNumber
                ? new GitCommitResult(false, null, "Injected commit failure.")
                : _inner.CommitDescriptor(root, descriptorRelPath, message);
        }
    }
}
