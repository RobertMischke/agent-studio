using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Jobs.Audit;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the contract for the completed-lane audit (Part 2+3 of the
/// consolidation/audit task):
///
/// <list type="bullet">
/// <item>Re-evaluation of a single card with a clean status returns <c>ok</c>.</item>
/// <item>A card whose prompt asks for code but carries no commit is <c>not-really-done</c>
/// and is bounced to <c>1b-needs-human-review</c> with a <c>quality_loop_reopened</c>
/// timeline event.</item>
/// <item>An async whole-project audit walks every completed/archive card, reports
/// per-card verdicts, and finishes within the test's small bound.</item>
/// </list>
/// </summary>
public class CompletedLaneAuditServiceTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _workspace;
    private readonly string _watchPath;

    public CompletedLaneAuditServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-audit-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReEvaluate_CleanCard_ReturnsOk_KeepsLane()
    {
        var (audit, scanner, store, _) = Build();
        // Code change asked + commit attached + status mentions tests.
        WriteJob(TaskStates.Completed, "good-card", "Good Card",
            promptBody: "Implement the foo helper and add tests for it.",
            statusBody: "Done. Added foo.cs with tests. All checks green.",
            commitSha: "abc1234");

        var outcome = audit.ReEvaluate("good-card", _watchPath, "tester");

        Assert.Equal(ReEvaluateStatus.Success, outcome.Status);
        Assert.Equal(AuditVerdicts.Ok, outcome.Response!.Verdict);
        Assert.Equal(TaskStates.Completed, outcome.Response.NewState);
        Assert.NotNull(scanner.FindJob("good-card", _watchPath));
        Assert.Equal(TaskStates.Completed, scanner.FindJob("good-card", _watchPath)!.State);
    }

    [Fact]
    public void ReEvaluate_PromptAsksForCodeButNoCommit_FlipsToNeedsHumanReview()
    {
        var (audit, scanner, _, _) = Build();
        WriteJob(TaskStates.Completed, "shaky-card", "Shaky Card",
            promptBody: "Fix the bug in OrderService where the total calculation is off.",
            statusBody: "All done.",
            commitSha: null);

        var outcome = audit.ReEvaluate("shaky-card", _watchPath, "tester");

        Assert.Equal(ReEvaluateStatus.Success, outcome.Status);
        Assert.Equal(AuditVerdicts.NotReallyDone, outcome.Response!.Verdict);
        Assert.Equal(TaskStates.NeedsHumanReview, outcome.Response.NewState);

        var moved = scanner.FindJob("shaky-card", _watchPath);
        Assert.NotNull(moved);
        Assert.Equal(TaskStates.NeedsHumanReview, moved!.State);

        // Quality-loop event landed on the moved folder's timeline.
        var timelinePath = TaskPaths.TimelineLog(moved.FolderPath);
        Assert.True(File.Exists(timelinePath));
        var contents = File.ReadAllText(timelinePath);
        Assert.Contains("quality_loop_reopened", contents);
    }

    [Fact]
    public void ReEvaluate_OutsideCompletedOrArchive_ReturnsWrongLane()
    {
        var (audit, _, _, _) = Build();
        WriteJob(TaskStates.Ready, "ready-card", "Ready Card", "x", "y", commitSha: null);

        var outcome = audit.ReEvaluate("ready-card", _watchPath, "tester");

        Assert.Equal(ReEvaluateStatus.WrongLane, outcome.Status);
    }

    [Fact]
    public async Task StartAudit_ProcessesAllCompletedCards()
    {
        var (audit, _, store, _) = Build();
        WriteJob(TaskStates.Completed, "good-1", "Good 1",
            "Implement helper and add tests.", "Done; helper.cs added with tests.", "aaa1");
        WriteJob(TaskStates.Completed, "shaky-1", "Shaky 1",
            "Fix the parser bug.", "Done.", null);
        WriteJob(TaskStates.Archive, "good-2", "Good 2",
            "Document the API.", "Done; docs updated.", "bbb2");

        var startOutcome = audit.StartAudit(_watchPath, "tester");
        Assert.Equal(AuditRunStartStatus.Success, startOutcome.Status);
        Assert.NotNull(startOutcome.RunId);

        // Wait up to 5 s for the audit to finish.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        CompletedLaneAuditRunStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = store.Get(startOutcome.RunId!);
            if (status?.Status == "finished") break;
            await Task.Delay(50);
        }

        Assert.NotNull(status);
        Assert.Equal("finished", status!.Status);
        Assert.Equal(3, status.Total);
        Assert.Equal(3, status.Processed);
        Assert.True(status.NotReallyDone >= 1,
            $"Expected at least one not-really-done verdict, got {status.NotReallyDone}");
        // Report rendering.
        var report = audit.BuildReport(_watchPath);
        Assert.NotNull(report);
        Assert.Contains("Completed-lane audit", report!.Markdown);
        Assert.Contains("Verdict counts", report.Markdown);
    }

    // ---- Helpers --------------------------------------------------------

    private (CompletedLaneAuditService audit, TaskScannerService scanner, AuditRunStore store, TaskStateMachine states) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var notifier = new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(scanner, clients, registry, notifier, NullLogger<TaskMutationService>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var detector = new AcceptanceEvidenceDetector();
        var store = new AuditRunStore();
        var audit = new CompletedLaneAuditService(
            scanner, states, mutations, timeline, detector, store, registry,
            NullLogger<CompletedLaneAuditService>.Instance);
        return (audit, scanner, store, states);
    }

    private void WriteJob(string state, string slug, string title, string promptBody, string statusBody, string? commitSha)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var jobJson = commitSha == null
            ? $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}"
            : $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"commits\":[{{\"sha\":\"{commitSha}\",\"message\":\"work\",\"authorEmail\":\"x@y\",\"at\":\"2026-05-29T12:00:00Z\",\"fileCount\":1}}]}}";
        File.WriteAllText(Path.Combine(dir, "job.json"), jobJson);
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), statusBody);
    }
}
