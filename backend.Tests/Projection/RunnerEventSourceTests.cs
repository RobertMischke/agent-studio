using AgentStudio.Projection;
using AgentStudio.Diagnostics;
using AgentStudio.Persistence;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RunnerEventSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"runner-event-source-{Guid.NewGuid():N}");

    [Fact]
    public void ReadRecords_NormalizesTypedLifecycleAndDiagnosticEvents()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "runner-events.jsonl");
        File.WriteAllLines(path,
        [
            """{"eventId":"session-1","kind":"runner.session.started","occurredAt":"2026-07-22T10:00:00Z","payload":{"sessionId":"sess-42","cli":"codex","model":"gpt-5.4-mini","thinkingLevel":"high"}}""",
            """{"eventId":"turn-1","kind":"runner.turn.completed","occurredAt":"2026-07-22T10:06:52Z","payloadJson":"{\"sessionId\":\"sess-42\",\"turnId\":\"turn-7\",\"durationMs\":412000,\"usage\":{\"inputTokens\":74192,\"outputTokens\":8331,\"reasoningTokens\":1024},\"implementationStatus\":\"completed\",\"pipelineStatus\":\"post-processing\"}"}""",
            """{"eventId":"warning-1","type":"runner.warning","timestamp":"2026-07-22T10:06:53Z","data":{"severity":"warning","code":"missing-path","message":"PATH did not include the CLI directory"}}""",
            "{\"eventId\":\"torn",
        ]);

        var records = RunnerEventSource.ReadRecords(path);

        Assert.Equal(3, records.Count);
        Assert.Equal("session.started", records[0].Kind);
        Assert.Equal("sess-42", records[0].SessionId);
        Assert.Equal("gpt-5.4-mini", records[0].Model);

        var completed = records[1];
        Assert.Equal("turn.completed", completed.Kind);
        Assert.Equal("turn-7", completed.TurnId);
        Assert.Equal(412_000, completed.DurationMs);
        Assert.Equal(74_192, completed.InputTokens);
        Assert.Equal(8_331, completed.OutputTokens);
        Assert.Equal(1_024, completed.ReasoningTokens);
        Assert.Equal("completed", completed.ImplementationStatus);
        Assert.Equal("post-processing", completed.PipelineStatus);

        Assert.Equal("diagnostic", records[2].Kind);
        Assert.Equal("missing-path", records[2].Code);
    }

    [Fact]
    public async Task ReadAsync_LeavesDiagnosticsOutOfTheMainProjection()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllLinesAsync(Path.Combine(logs, "runner-events.jsonl"),
        [
            """{"kind":"session.completed","timestamp":"2026-07-22T10:00:00Z","payload":{"sessionId":"sess-42"}}""",
            """{"kind":"diagnostic","timestamp":"2026-07-22T10:00:01Z","payload":{"message":"plugin warning"}}""",
        ]);

        var projected = await new RunnerEventSource().ReadAsync(
            new TaskInfo { Id = "AGT-2204", FolderPath = _root },
            CancellationToken.None);

        var item = Assert.Single(projected);
        Assert.Equal("session.completed", item.Summary);
        Assert.Equal(string.Empty, item.BodyMarkdown);
    }

    [Fact]
    public async Task Journal_PersistsTypedEventsAndReplayDeduplicatesRetriesByEventId()
    {
        var journal = new RunnerEventJournal(new JsonlAppender());
        var task = new TaskInfo { Id = "AGT-2204", FolderPath = _root };
        var recorded = new RunnerRecordedEvent
        {
            Id = "turn-completed-7",
            Kind = "turn.completed",
            Timestamp = new DateTime(2026, 7, 22, 10, 6, 52, DateTimeKind.Utc),
            SessionId = "sess-42",
            TurnId = "turn-7",
            Model = "gpt-5.4-mini",
            ThinkingLevel = "high",
            DurationMs = 412_000,
            InputTokens = 74_192,
            OutputTokens = 8_331,
        };

        await journal.AppendAsync(task, recorded);
        await journal.AppendAsync(task, recorded);

        var replay = RunnerEventSource.ReadRecords(task);
        var item = Assert.Single(replay);
        Assert.Equal("turn.completed", item.Kind);
        Assert.Equal("sess-42", item.SessionId);
        Assert.Equal(74_192, item.InputTokens);
        Assert.Equal(8_331, item.OutputTokens);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
