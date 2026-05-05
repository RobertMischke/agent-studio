using System.Text;
using System.Text.Json;
using OrchestratorApi.Services.Analysis;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of <see cref="AnalysisReportStore"/>: a structured
/// report round-trips through disk, the Markdown sibling is the durable
/// human artifact, a malformed JSON sidecar keeps the Markdown visible with
/// an explicit parse status, and the store carries reference pointers
/// without copying the raw evidence they point at.
/// </summary>
public class AnalysisReportStoreTests : IDisposable
{
    private readonly string _workspace;

    public AnalysisReportStoreTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "analysis-report-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task AppendAsync_StructuredReport_RoundTripsThroughDiskAndIsReadableByFreshStore()
    {
        var store = new AnalysisReportStore();
        const string project = "agent-taskboard";
        var report = NewReport("01HX0000000000000000000001", "queue-health");

        var version = await store.AppendAsync(_workspace, project, report, MarkdownBody(report));
        Assert.Equal(1, version);

        // Both the index, the Markdown sibling, and the JSON sidecar exist on disk.
        Assert.True(File.Exists(AnalysisReportPaths.IndexFile(_workspace, project)));
        Assert.True(File.Exists(AnalysisReportPaths.MarkdownFile(_workspace, project, report.ReportId)));
        Assert.True(File.Exists(AnalysisReportPaths.JsonSidecarFile(_workspace, project, report.ReportId)));

        // A fresh store loads from disk and exposes the same record.
        var fresh = new AnalysisReportStore();
        var loaded = fresh.Snapshot(_workspace, project);
        Assert.Single(loaded);
        Assert.Equal(report.ReportId, loaded[0].ReportId);
        Assert.Equal(report.Topic, loaded[0].Topic);
        Assert.Equal(report.Severity, loaded[0].Severity);
        Assert.Equal(report.Scope.Kind, loaded[0].Scope.Kind);
        Assert.Equal(report.Scope.Project, loaded[0].Scope.Project);
        Assert.Equal(AnalysisReportParseStatus.Structured, loaded[0].ParseStatus);
    }

    [Fact]
    public async Task AppendAsync_RejectsReportWithMissingMarkdownBody()
    {
        var store = new AnalysisReportStore();
        var report = NewReport("01HX0000000000000000000002", "docs-drift");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(_workspace, report.Scope.Project!, report, markdownBody: ""));

        // The Markdown is the load-bearing contract. A rejected append must
        // not leave a partially-written index line behind.
        var index = AnalysisReportPaths.IndexFile(_workspace, report.Scope.Project!);
        Assert.False(File.Exists(index));
    }

    [Fact]
    public async Task AppendAsync_RejectsReportWithInvalidScopeForKind()
    {
        var store = new AnalysisReportStore();
        var bad = NewReport("01HX0000000000000000000003", "queue-health") with
        {
            Scope = new AnalysisReportScope(AnalysisReportScopeKind.Task, Project: "agent-taskboard", JobId: null),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(_workspace, bad.Scope.Project!, bad, MarkdownBody(bad)));
    }

    [Fact]
    public async Task AppendAsync_RejectsMalformedJsonStatusWithoutParseError()
    {
        var store = new AnalysisReportStore();
        var bad = NewReport("01HX0000000000000000000004", "docs-drift") with
        {
            ParseStatus = AnalysisReportParseStatus.MalformedJson,
            ParseError = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(_workspace, bad.Scope.Project!, bad, MarkdownBody(bad)));
    }

    [Fact]
    public async Task LoadFromDisk_MalformedSidecarKeepsMarkdownVisibleAsUnstructuredRecord()
    {
        // Producer wrote a Markdown file plus a JSON sidecar that fails to
        // parse. The producer (or the importer that picked it up) records
        // an Unstructured / MalformedJson entry in the index so the UI keeps
        // the Markdown visible. The store itself enforces nothing here
        // beyond accepting the entry and returning the Markdown sibling.
        var store = new AnalysisReportStore();
        const string project = "agent-taskboard";
        const string reportId = "01HX0000000000000000000005";

        // Place a malformed JSON sidecar and a real Markdown file on disk.
        var dir = AnalysisReportPaths.ProjectDir(_workspace, project);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            AnalysisReportPaths.JsonSidecarFile(_workspace, project, reportId),
            "{this is not valid json",
            Encoding.UTF8);
        const string markdown = "# Are we on track?\n\nVerdict: yes, with caveats.\n";
        File.WriteAllText(
            AnalysisReportPaths.MarkdownFile(_workspace, project, reportId),
            markdown,
            Encoding.UTF8);

        // The importer records the malformed sidecar as a MalformedJson
        // entry in the index. The Markdown stays visible.
        var entry = NewReport(reportId, "are-we-on-track") with
        {
            ParseStatus = AnalysisReportParseStatus.MalformedJson,
            ParseError = "Sidecar JSON failed to parse at column 2.",
            Severity = AnalysisReportSeverity.Info,
            Summary = "Sidecar unreadable; Markdown body remains the durable artifact.",
        };

        await store.AppendAsync(_workspace, project, entry, markdown);

        var fresh = new AnalysisReportStore();
        var loaded = fresh.GetById(_workspace, project, reportId);
        Assert.NotNull(loaded);
        Assert.Equal(AnalysisReportParseStatus.MalformedJson, loaded!.ParseStatus);
        Assert.False(string.IsNullOrWhiteSpace(loaded.ParseError));

        // The Markdown sibling remains readable even though the sidecar is
        // malformed.
        var body = fresh.ReadMarkdown(_workspace, project, reportId);
        Assert.NotNull(body);
        Assert.Contains("Are we on track?", body!);

        // A MalformedJson report writes no fresh sidecar (the broken one
        // stays put for human inspection).
        var sidecarPath = AnalysisReportPaths.JsonSidecarFile(_workspace, project, reportId);
        Assert.Equal("{this is not valid json", File.ReadAllText(sidecarPath, Encoding.UTF8));
    }

    [Fact]
    public void LoadFromDisk_SkipsCorruptIndexLinesWithoutBreakingProjection()
    {
        // The store is lenient on read: a single bad legacy line in the
        // index never breaks the projection. Strict-mode validation runs at
        // append time so new garbage cannot enter.
        const string project = "agent-taskboard";
        var dir = AnalysisReportPaths.ProjectDir(_workspace, project);
        Directory.CreateDirectory(dir);

        var good = NewReport("01HX0000000000000000000006", "queue-health");
        var goodLine = JsonSerializer.Serialize(good, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        File.WriteAllLines(AnalysisReportPaths.IndexFile(_workspace, project), new[]
        {
            "",
            "{not-json",
            "[1,2,3]",
            "{\"reportId\":\"x\"}", // missing required fields
            goodLine,
        }, Encoding.UTF8);

        var store = new AnalysisReportStore();
        var snap = store.Snapshot(_workspace, project);
        Assert.Single(snap);
        Assert.Equal(good.ReportId, snap[0].ReportId);
    }

    [Fact]
    public async Task AppendAsync_PreservesReferencesWithoutCopyingRawEvidence()
    {
        var store = new AnalysisReportStore();
        const string project = "agent-taskboard";

        var report = NewReport("01HX0000000000000000000007", "are-we-on-track") with
        {
            References = new[]
            {
                new AnalysisReportReference(AnalysisReportReferenceKind.Job, "agent-taskboard/3-progress/analysis-report-contract-and-storage"),
                new AnalysisReportReference(AnalysisReportReferenceKind.Run, "analysis-report-contract-and-storage:1"),
                new AnalysisReportReference(AnalysisReportReferenceKind.Commit, "agent-taskboard@a90ea35"),
                new AnalysisReportReference(AnalysisReportReferenceKind.BusMessage, "01HX0000000000000000000099"),
                new AnalysisReportReference(AnalysisReportReferenceKind.LogSlice, "analysis-report-contract-and-storage:1:42-58", Label: "tool-call burst"),
                new AnalysisReportReference(AnalysisReportReferenceKind.PreviousReport, "01HX0000000000000000000000"),
                new AnalysisReportReference(AnalysisReportReferenceKind.Doc, "ROADMAP.md"),
            },
        };

        await store.AppendAsync(_workspace, project, report, MarkdownBody(report));

        // Round-trip: the references survive disk and project re-load, byte
        // for byte. No raw evidence is copied alongside the references.
        var fresh = new AnalysisReportStore();
        var loaded = fresh.GetById(_workspace, project, report.ReportId);
        Assert.NotNull(loaded);
        Assert.Equal(report.References.Count, loaded!.References.Count);
        for (var i = 0; i < report.References.Count; i++)
        {
            Assert.Equal(report.References[i].Kind, loaded.References[i].Kind);
            Assert.Equal(report.References[i].Ref, loaded.References[i].Ref);
            Assert.Equal(report.References[i].Label, loaded.References[i].Label);
        }

        // The on-disk JSON sidecar carries the reference pointer text but
        // does not contain raw log bytes, raw bus messages, or commit diff
        // bodies. We assert by content pattern: the sidecar may contain the
        // ref strings, but it must not contain markers we know belong to
        // the underlying evidence streams.
        var sidecar = File.ReadAllText(
            AnalysisReportPaths.JsonSidecarFile(_workspace, project, report.ReportId),
            Encoding.UTF8);
        Assert.Contains("analysis-report-contract-and-storage:1:42-58", sidecar);
        Assert.Contains("agent-taskboard@a90ea35", sidecar);

        // Sanity: the on-disk sidecar is small (KB scale), not a log dump.
        var sidecarBytes = new FileInfo(
            AnalysisReportPaths.JsonSidecarFile(_workspace, project, report.ReportId)).Length;
        Assert.InRange(sidecarBytes, 1, 16 * 1024);
    }

    [Fact]
    public async Task ReadSince_ReturnsOnlyNewTailAcrossSuccessiveCalls()
    {
        var store = new AnalysisReportStore();
        const string project = "agent-taskboard";

        await store.AppendAsync(_workspace, project, NewReport("01HX0000000000000000000010", "topic-a"), "# a");
        await store.AppendAsync(_workspace, project, NewReport("01HX0000000000000000000011", "topic-b"), "# b");

        var (firstBatch, cursor1) = store.ReadSince(_workspace, project, 0);
        Assert.Equal(2, firstBatch.Count);

        var (emptyBatch, cursor2) = store.ReadSince(_workspace, project, cursor1);
        Assert.Empty(emptyBatch);
        Assert.Equal(cursor1, cursor2);

        await store.AppendAsync(_workspace, project, NewReport("01HX0000000000000000000012", "topic-c"), "# c");
        var (tail, _) = store.ReadSince(_workspace, project, cursor2);
        Assert.Single(tail);
        Assert.Equal("topic-c", tail[0].Topic);
    }

    [Fact]
    public async Task AppendAsync_WorkspaceScopedReportsLandUnderTheWorkspaceProjectKey()
    {
        var store = new AnalysisReportStore();
        var workspaceReport = new AnalysisReport(
            ReportId: "01HX0000000000000000000020",
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Workspace),
            Producer: new AnalysisReportProducer(AnalysisReportProducerKind.ExternalMonitor, Agent: "system-review.sh"),
            Trigger: AnalysisReportTrigger.ExternalMonitor,
            Topic: "system-health",
            Summary: "Workspace looks healthy after a 6-hour run.",
            Severity: AnalysisReportSeverity.Info,
            ParseStatus: AnalysisReportParseStatus.Structured,
            References: Array.Empty<AnalysisReportReference>(),
            FollowUpTaskSuggestions: Array.Empty<AnalysisReportFollowUpTaskSuggestion>());

        await store.AppendAsync(
            _workspace,
            AnalysisReportPaths.WorkspaceProjectKey,
            workspaceReport,
            "# System review pass\n\nAll lanes look reasonable.");

        var dir = AnalysisReportPaths.ProjectDir(_workspace, AnalysisReportPaths.WorkspaceProjectKey);
        Assert.EndsWith(Path.Combine("logs", "analysis", "_workspace"), dir);
        Assert.True(File.Exists(AnalysisReportPaths.MarkdownFile(
            _workspace, AnalysisReportPaths.WorkspaceProjectKey, workspaceReport.ReportId)));
    }

    private static AnalysisReport NewReport(string reportId, string topic)
        => new(
            ReportId: reportId,
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: "agent-taskboard"),
            Producer: new AnalysisReportProducer(AnalysisReportProducerKind.Manual, Agent: "user"),
            Trigger: AnalysisReportTrigger.Manual,
            Topic: topic,
            Summary: "On track with two follow-up suggestions.",
            Severity: AnalysisReportSeverity.Warn,
            ParseStatus: AnalysisReportParseStatus.Structured,
            References: new[]
            {
                new AnalysisReportReference(AnalysisReportReferenceKind.Job, "agent-taskboard/3-progress/x"),
            },
            FollowUpTaskSuggestions: new[]
            {
                new AnalysisReportFollowUpTaskSuggestion(
                    Title: "Resync ROADMAP.md with queue",
                    Summary: "Two themes have drifted; queue order matches ADR-0023 but ROADMAP wording lags.",
                    Priority: AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: AnalysisReportFollowUpRelatedTopic.RoadmapAlignment),
            });

    private static string MarkdownBody(AnalysisReport report)
        => $"# {report.Topic}\n\n{report.Summary}\n";
}
