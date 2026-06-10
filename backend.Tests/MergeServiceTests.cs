using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the contract for the consolidation/merge API. Covers the
/// load-bearing flows from the task spec: candidate detection, dry-run
/// preview, real consolidate (folder archived, timeline events
/// mirrored, audit row written), and the 24h restore-by-token undo.
/// </summary>
public class MergeServiceTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _workspace;
    private readonly string _watchPath;

    public MergeServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-merge-tests-" + Guid.NewGuid().ToString("N"));
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
    public void Candidates_FindsWrapperByPromptMention()
    {
        var (merges, _, _) = Build();
        WriteJob(TaskStates.Completed, "primary-card", "Primary",
            "## Goal\nThis is the primary card. It does the work and ships changes.");
        WriteJob(TaskStates.Backlog, "human-decision-needed-primary-card", "Wrapper",
            "Wrapper around primary-card. Asks the user to make a decision.");
        WriteJob(TaskStates.Ready, "unrelated-thing", "Unrelated", "Totally different task.");

        var result = merges.FindCandidates("primary-card", _watchPath);

        Assert.Contains(result.Candidates, c => c.Id == "human-decision-needed-primary-card");
        Assert.DoesNotContain(result.Candidates, c => c.Id == "unrelated-thing");
    }

    [Fact]
    public void Preview_ReturnsProposedEventsWithoutTouchingDisk()
    {
        var (merges, _, scanner) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "Primary prompt");
        WriteJob(TaskStates.Backlog, "wrapper", "Wrapper", "Refers to primary");
        AppendTimelineEvent("wrapper", "agent_run_started", "Run started");

        var outcome = merges.Preview("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "wrapper",
            Mode = MergeModes.Consolidate,
            Reason = "wrapper of primary",
        });

        Assert.Equal(MergeStatus.Success, outcome.Status);
        // Preview is opaque-serialised into outcome.Message - parse it
        // and assert the timeline events came through.
        Assert.NotNull(outcome.Message);
        Assert.Contains("merged_in", outcome.Message);
        Assert.Contains("agent_run_started", outcome.Message);
        // Secondary still exists on disk.
        Assert.NotNull(scanner.FindJob("wrapper", _watchPath));
    }

    [Fact]
    public void Merge_Consolidate_ArchivesSecondaryAndAppendsTimeline()
    {
        var (merges, audit, scanner) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "Primary prompt");
        WriteJob(TaskStates.Backlog, "wrapper", "Wrapper",
            "This is a wrapper task that references the primary card.");
        AppendTimelineEvent("wrapper", "agent_run_started", "Wrapper run started");
        AppendTimelineEvent("wrapper", "agent_run_finished", "Wrapper run finished");
        AppendTimelineEvent("wrapper", "agent_run_started", "Wrapper run 2 started");

        var outcome = merges.Merge("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "wrapper",
            Mode = MergeModes.Consolidate,
            Reason = "ASS-30 wrapper of ASS-182",
        }, who: "tester@example.com");

        Assert.Equal(MergeStatus.Success, outcome.Status);
        Assert.NotNull(outcome.Response);
        // 1 merged_in summary + 3 mirrored events = 4 appended events.
        Assert.True(outcome.Response!.TimelineEventsAppended >= 4,
            $"Expected >= 4 timeline events, got {outcome.Response.TimelineEventsAppended}");
        Assert.NotEmpty(outcome.Response.RestoreToken);
        Assert.True(outcome.Response.UndoExpiresAt > DateTime.UtcNow.AddHours(23));

        // Secondary folder is gone from the active board.
        Assert.Null(scanner.FindJob("wrapper", _watchPath));

        // Archived folder exists under .archive/merged.
        var archiveRoot = Path.Combine(_workspace, ".archive", "merged");
        Assert.True(Directory.Exists(archiveRoot));
        var archived = Directory.GetDirectories(archiveRoot);
        Assert.Single(archived);
        Assert.StartsWith("wrapper__", Path.GetFileName(archived[0]));

        // Primary timeline now has the mirrored events.
        var timeline = ReadTimeline("primary");
        Assert.Contains(timeline, e => e.Kind == "merged_in" && e.Summary.Contains("wrapper"));
        Assert.Contains(timeline, e => e.Kind == "agent_run_started" && e.Summary.Contains("[from wrapper]"));

        // Audit log has exactly one entry with the restore token.
        var records = audit.ReadAll();
        Assert.Single(records);
        Assert.Equal(outcome.Response.RestoreToken, records[0].RestoreToken);
        Assert.Equal("primary", records[0].PrimaryId);
        Assert.Equal("wrapper", records[0].SecondaryId);
    }

    [Fact]
    public void Merge_Consolidate_Undo_RestoresArchivedFolder()
    {
        var (merges, audit, scanner) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "Primary prompt");
        WriteJob(TaskStates.Backlog, "wrapper", "Wrapper", "Wrapper text");

        var mergeOutcome = merges.Merge("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "wrapper",
            Mode = MergeModes.Consolidate,
            Reason = "test",
        }, who: "tester@example.com");
        Assert.Equal(MergeStatus.Success, mergeOutcome.Status);
        Assert.Null(scanner.FindJob("wrapper", _watchPath));

        var undoOutcome = merges.Undo("primary", new MergeUndoRequest
        {
            RestoreToken = mergeOutcome.Response!.RestoreToken,
        }, who: "tester@example.com");

        Assert.Equal(MergeUndoStatus.Success, undoOutcome.Status);
        Assert.True(undoOutcome.Response!.Restored);

        // Wrapper is back in its original lane (0-backlog).
        var restored = scanner.FindJob("wrapper", _watchPath);
        Assert.NotNull(restored);
        Assert.Equal(TaskStates.Backlog, restored!.State);

        // Audit log now has 2 rows: the original + the undo marker.
        var records = audit.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.NotNull(records[1].UndoneAt);
    }

    [Fact]
    public void Undo_WithUnknownToken_ReturnsTokenNotFound()
    {
        var (merges, _, _) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "Primary prompt");

        var outcome = merges.Undo("primary",
            new MergeUndoRequest { RestoreToken = "deadbeef" },
            who: "tester@example.com");

        Assert.Equal(MergeUndoStatus.TokenNotFound, outcome.Status);
    }

    [Fact]
    public void Merge_RejectsSameJob()
    {
        var (merges, _, _) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "x");

        var outcome = merges.Merge("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "primary",
        }, who: "tester");

        Assert.Equal(MergeStatus.SameJob, outcome.Status);
    }

    [Fact]
    public void Merge_RejectsMissingSecondary()
    {
        var (merges, _, _) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "x");

        var outcome = merges.Merge("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "ghost",
        }, who: "tester");

        Assert.Equal(MergeStatus.SecondaryNotFound, outcome.Status);
    }

    [Fact]
    public void Merge_LinkOnly_SetsMergedIntoFieldWithoutMoving()
    {
        var (merges, _, scanner) = Build();
        WriteJob(TaskStates.Ready, "primary", "Primary", "x");
        WriteJob(TaskStates.Backlog, "linked", "Linked", "y");

        var outcome = merges.Merge("primary", _watchPath, new MergeRequest
        {
            SecondaryId = "linked",
            Mode = MergeModes.LinkOnly,
            Reason = "link only",
        }, who: "tester");

        Assert.Equal(MergeStatus.Success, outcome.Status);
        // Secondary is still in its original lane.
        var still = scanner.FindJob("linked", _watchPath);
        Assert.NotNull(still);
        Assert.Equal(TaskStates.Backlog, still!.State);

        // task.json carries mergedInto pointer.
        var json = File.ReadAllText(Path.Combine(still.FolderPath, "task.json"));
        Assert.Contains("\"mergedInto\":", json);
        Assert.Contains("primary", json);
    }

    // ---- Helpers --------------------------------------------------------

    private (MergeService merges, MergeAuditLog audit, TaskScannerService scanner) Build()
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
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var audit = new MergeAuditLog(config, scanner, NullLogger<MergeAuditLog>.Instance);
        var candidates = new MergeCandidateFinder(scanner);
        var merges = new MergeService(scanner, states, timeline, audit, candidates, NullLogger<MergeService>.Instance);
        return (merges, audit, scanner);
    }

    private void WriteJob(string state, string slug, string title, string promptBody)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
    }

    private void AppendTimelineEvent(string slug, string kind, string summary)
    {
        var jobDir = FindJobDir(slug);
        var logsDir = TaskPaths.LogsDir(jobDir);
        Directory.CreateDirectory(logsDir);
        var line = System.Text.Json.JsonSerializer.Serialize(new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = kind,
            Actor = "agent",
            Summary = summary,
        }, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        File.AppendAllText(TaskPaths.TimelineLog(jobDir), line + Environment.NewLine);
    }

    private List<TimelineEvent> ReadTimeline(string slug)
    {
        var jobDir = FindJobDir(slug);
        var path = TaskPaths.TimelineLog(jobDir);
        if (!File.Exists(path)) return [];
        var result = new List<TimelineEvent>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = System.Text.Json.JsonSerializer.Deserialize<TimelineEvent>(line,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt != null) result.Add(evt);
            }
            catch { }
        }
        return result;
    }

    private string FindJobDir(string slug)
    {
        foreach (var state in TaskStates.All)
        {
            var dir = Path.Combine(_watchPath, state, slug);
            if (Directory.Exists(dir)) return dir;
        }
        throw new InvalidOperationException($"No folder for {slug}");
    }
}
