using System.Text;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Product Runtime Observability capture surface: events round-trip
/// through disk, malformed lines surface as parse warnings instead of throwing,
/// the stdout adapter is library-agnostic, and warnings sit in a sidecar file
/// next to the source so reviewers can inspect them.
/// </summary>
public sealed class RuntimeEventCaptureTests : IDisposable
{
    private readonly string _root;

    public RuntimeEventCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "runtime-capture-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static ProductRuntimeEvent Make(string ev = "http.request.completed", string subsystem = "backend",
        string level = "Info", DateTime? ts = null)
    {
        return new ProductRuntimeEvent
        {
            Timestamp = ts ?? new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
            Level = level,
            Event = ev,
            Subsystem = subsystem,
        };
    }

    [Fact]
    public async Task Writer_AppendsToJobDayFile_AndReaderRoundTripsEvent()
    {
        var jobFolder = Path.Combine(_root, "3-progress", "feature-x");
        var writer = new RuntimeEventWriter();
        var evt = Make() with
        {
            Operation = "GET /api/tasks",
            Status = "Ok",
            Duration = new ProductRuntimeEventDuration { Ms = 12.5, StartedAt = new DateTime(2026, 5, 6, 11, 59, 59, DateTimeKind.Utc) },
            Tags = new[] { "ui-polled" },
        };

        await writer.AppendToJobAsync(jobFolder, evt);

        var path = RuntimeEventPaths.TaskDayFile(jobFolder, evt.Timestamp);
        Assert.True(File.Exists(path));

        var result = new RuntimeEventReader().Read(path);
        Assert.Empty(result.Warnings);
        Assert.Single(result.Events);
        var read = result.Events[0];
        Assert.Equal("http.request.completed", read.Event);
        Assert.Equal("Ok", read.Status);
        Assert.Equal(12.5, read.Duration!.Ms);
        Assert.Contains("ui-polled", read.Tags!);
    }

    [Fact]
    public async Task Writer_RejectsInvalidEvent_BeforeWriting()
    {
        var jobFolder = Path.Combine(_root, "3-progress", "feature-y");
        var writer = new RuntimeEventWriter();
        var bad = Make() with { Level = "Bogus" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendToJobAsync(jobFolder, bad));

        var path = RuntimeEventPaths.TaskDayFile(jobFolder, bad.Timestamp);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Reader_PreservesGoodEvents_AndReportsParseWarnings()
    {
        var jobFolder = Path.Combine(_root, "3-progress", "feature-z");
        var path = RuntimeEventPaths.TaskDayFile(jobFolder, new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var good = JsonSerializer.Serialize(Make(ev: "render.first-paint", subsystem: "frontend"), RuntimeEventReader.JsonOptions);
        File.WriteAllLines(path, new[]
        {
            "",                                                                              // ignored
            "{not json",                                                                     // parse warning
            "[1,2,3]",                                                                       // parse warning (array, not object)
            "{\"schemaVersion\":1,\"timestamp\":\"2026-05-06T12:00:00Z\",\"level\":\"Bogus\",\"event\":\"x\",\"subsystem\":\"frontend\"}", // validation warning
            good,
        }, Encoding.UTF8);

        var result = new RuntimeEventReader().Read(path);

        Assert.Single(result.Events);
        Assert.Equal("render.first-paint", result.Events[0].Event);

        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal(path, w.SourcePath));
        Assert.All(result.Warnings, w => Assert.False(string.IsNullOrEmpty(w.RawLine)));
        Assert.Contains(result.Warnings, w => w.Reason.Contains("json parse"));
        Assert.Contains(result.Warnings, w => w.Reason.Contains("validation"));
    }

    [Fact]
    public async Task Writer_AppendWarning_WritesSidecarBesideJsonl()
    {
        var jobFolder = Path.Combine(_root, "3-progress", "feature-warn");
        var writer = new RuntimeEventWriter();
        var day = new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc);
        var path = RuntimeEventPaths.TaskDayFile(jobFolder, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var warning = new RuntimeEventParseWarning(path, 7, "json parse: unexpected token", "{not json");
        await writer.AppendWarningAsync(path, warning);

        var sidecar = RuntimeEventPaths.WarningsFile(path);
        Assert.True(File.Exists(sidecar));
        var line = File.ReadAllLines(sidecar).Single();
        Assert.Contains("\"lineNumber\":7", line);
        Assert.Contains("\"rawLine\":\"{not json\"", line);
    }

    [Fact]
    public void StdoutAdapter_KeepsPlainLogLines_OutOfRuntimeStream()
    {
        var raw = new[]
        {
            "INFO 2026-05-06T12:00:00Z my-app starting",
            "{\"schemaVersion\":1,\"timestamp\":\"2026-05-06T12:00:00Z\",\"level\":\"Info\",\"event\":\"app.started\",\"subsystem\":\"backend\"}",
            "  starting websocket listener on :5030",
            "{\"schemaVersion\":1,\"timestamp\":\"2026-05-06T12:00:01Z\",\"level\":\"Warn\",\"event\":\"queue.depth.high\",\"subsystem\":\"runner\",\"payload\":{\"depth\":42}}",
        };

        var result = RuntimeEventStdoutAdapter.Ingest(raw, sourceLabel: "backend.stdout");

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal("app.started", result.Events[0].Event);
        Assert.Equal("queue.depth.high", result.Events[1].Event);
        // Payload survives.
        var payload = result.Events[1].Payload!.Value;
        Assert.Equal(42, payload.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void StdoutAdapter_ReportsParseWarning_ForJsonLikeButInvalid()
    {
        var raw = new[]
        {
            "INFO 2026-05-06T12:00:00Z plain log line",
            "{\"msg\":\"hi\"}",                                                              // parses, but not a runtime event (missing required fields)
            "{not really json",                                                              // looks like JSON, fails to parse
            "{\"schemaVersion\":1,\"timestamp\":\"2026-05-06T12:00:00Z\",\"level\":\"Bogus\",\"event\":\"e\",\"subsystem\":\"s\"}", // schema rejects level
        };

        var result = RuntimeEventStdoutAdapter.Ingest(raw, sourceLabel: "backend.stdout");

        Assert.Empty(result.Events);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Reason.Contains("json parse") && w.RawLine.Contains("\"msg\":\"hi\""));
        Assert.Contains(result.Warnings, w => w.Reason.Contains("json parse") && w.RawLine.Contains("not really json"));
        Assert.Contains(result.Warnings, w => w.Reason.Contains("validation"));
        Assert.All(result.Warnings, w => Assert.Equal("backend.stdout", w.SourcePath));
    }

    [Fact]
    public async Task WorkspaceLayout_RoutesByProjectAndUtcDay()
    {
        var workspace = Path.Combine(_root, "workspace");
        var writer = new RuntimeEventWriter();
        var ts = new DateTime(2026, 5, 6, 23, 30, 0, DateTimeKind.Utc);
        var evtA = Make(ts: ts);
        var evtB = Make(ts: ts.AddDays(1));

        await writer.AppendToWorkspaceAsync(workspace, "agent-taskboard", evtA);
        await writer.AppendToWorkspaceAsync(workspace, "agent-taskboard", evtB);
        await writer.AppendToWorkspaceAsync(workspace, project: null, evtA);

        var dayA = RuntimeEventPaths.WorkspaceDayFile(workspace, "agent-taskboard", ts);
        var dayB = RuntimeEventPaths.WorkspaceDayFile(workspace, "agent-taskboard", ts.AddDays(1));
        var workspaceA = RuntimeEventPaths.WorkspaceDayFile(workspace, project: null, ts);

        Assert.True(File.Exists(dayA));
        Assert.True(File.Exists(dayB));
        Assert.True(File.Exists(workspaceA));
        Assert.NotEqual(dayA, dayB);
        Assert.Contains(Path.Combine("logs", "runtime", "agent-taskboard"), dayA);
        Assert.Contains(Path.Combine("logs", "runtime", RuntimeEventPaths.WorkspaceScope), workspaceA);
    }

    [Fact]
    public void Reader_ReturnsEmpty_WhenFileMissing()
    {
        var path = Path.Combine(_root, "no-such-file.jsonl");
        var result = new RuntimeEventReader().Read(path);
        Assert.Empty(result.Events);
        Assert.Empty(result.Warnings);
    }
}
