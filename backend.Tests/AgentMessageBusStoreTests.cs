using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using Xunit;
using Xunit.Abstractions;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the Agent Message Bus store contract: append round-trips through
/// disk, malformed lines do not break the projection, query filters work
/// across the documented dimensions, and a 10k-message project loads and
/// queries fast enough for the in-memory layer to be acceptable.
/// </summary>
public class AgentMessageBusStoreTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workspace;

    public AgentMessageBusStoreTests(ITestOutputHelper output)
    {
        _out = output;
        _workspace = Path.Combine(Path.GetTempPath(), "bus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task AppendAsync_PersistsToDayFileAndReadsBack()
    {
        var store = new AgentMessageBusStore();
        var msg = NewMessage("01HXYZ0000000000000000A001", "agent-taskboard", kind: "decision", role: "actor");

        await store.AppendAsync(_workspace, msg);

        var path = AgentMessageBusPaths.DayFile(_workspace, "agent-taskboard", msg.CreatedAt.Date);
        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert.Single(lines);

        // Cold reader: a fresh store loads from disk.
        var reader = new AgentMessageBusStore();
        var found = reader.GetById(_workspace, "agent-taskboard", msg.Id);
        Assert.NotNull(found);
        Assert.Equal(msg.Summary, found!.Summary);
    }

    [Fact]
    public async Task AppendAsync_RejectsInvalidMessage()
    {
        var store = new AgentMessageBusStore();
        var bad = new AgentMessage
        {
            Id = "01HXYZ0000000000000000B001",
            CreatedAt = DateTime.UtcNow,
            ParticipantId = "user",
            Role = "actor",
            Kind = "not-a-kind",
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(_workspace, bad));
    }

    [Fact]
    public void LoadFromDisk_SkipsMalformedLines()
    {
        var project = "agent-taskboard";
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var good = JsonSerializer.Serialize(
            NewMessage("01HXYZ0000000000000000C001", project, createdAt: day.AddHours(1)),
            AgentMessageBusStore.SerializerOptions);
        // 4 bad lines around the good one: empty, junk, non-object, missing required.
        File.WriteAllLines(path, new[]
        {
            "",
            "{not json",
            "[1,2,3]",
            "{\"id\":\"too-short\"}",
            good,
        }, Encoding.UTF8);

        var store = new AgentMessageBusStore();
        var all = store.Recent(_workspace, project, limit: 100);
        Assert.Single(all);
        Assert.Equal("01HXYZ0000000000000000C001", all[0].Id);
    }

    [Fact]
    public async Task Query_FiltersByJobRunParticipantKindSeverityTimeAndTag()
    {
        var store = new AgentMessageBusStore();
        var project = "agent-taskboard";
        var t0 = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);

        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000D001", project,
            kind: "lifecycle", role: "system", jobId: "job-a", runId: "run-1",
            participantId: "runtime:taskboard", severity: "Info", createdAt: t0, tags: new[] { "ui" }));
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000D002", project,
            kind: "decision", role: "actor", jobId: "job-a", runId: "run-1",
            participantId: "orchestrator", severity: "Warn", createdAt: t0.AddMinutes(1), tags: new[] { "policy" }));
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000D003", project,
            kind: "advisory", role: "system", jobId: "job-b", runId: "run-2",
            participantId: "supervisor:agent-taskboard", severity: "High", createdAt: t0.AddMinutes(2)));

        Assert.Equal(2, store.Query(_workspace, project, new AgentMessageQuery(JobId: "job-a")).Count);
        Assert.Equal(2, store.Query(_workspace, project, new AgentMessageQuery(RunId: "run-1")).Count);
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(ParticipantId: "orchestrator")));
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Kind: "advisory")));
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Severity: "High")));
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Tag: "ui")));
        Assert.Equal(2, store.Query(_workspace, project, new AgentMessageQuery(Since: t0.AddSeconds(30))).Count);
        Assert.Equal(2, store.Query(_workspace, project, new AgentMessageQuery(Until: t0.AddMinutes(1).AddSeconds(30))).Count);
    }

    [Fact]
    public async Task Query_FiltersByCliAndSkillThroughParticipantRegistry()
    {
        var store = new AgentMessageBusStore();
        var project = "agent-taskboard";
        await store.RegisterParticipantAsync(_workspace, new AgentParticipant
        {
            Id = "agent:claude",
            Kind = "CodingAgent",
            DisplayName = "Claude",
            Cli = "claude",
        });
        await store.RegisterParticipantAsync(_workspace, new AgentParticipant
        {
            Id = "support:security-audit",
            Kind = "SupportingAgent",
            DisplayName = "Security Audit",
            Cli = "codex",
            Skill = "security-review",
        });

        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000E001", project,
            participantId: "agent:claude"));
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000E002", project,
            participantId: "support:security-audit"));

        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Cli: "claude")));
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Cli: "codex")));
        Assert.Single(store.Query(_workspace, project, new AgentMessageQuery(Skill: "security-review")));
    }

    [Fact]
    public async Task Summary_AggregatesCountsAndTimespan()
    {
        var store = new AgentMessageBusStore();
        var project = "agent-taskboard";
        var t0 = new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc);
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000F001", project,
            kind: "lifecycle", participantId: "runtime:taskboard", createdAt: t0));
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000F002", project,
            kind: "decision", participantId: "orchestrator", severity: "Warn", createdAt: t0.AddMinutes(5)));
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000F003", project,
            kind: "lifecycle", participantId: "runtime:taskboard", createdAt: t0.AddMinutes(10)));

        var summary = store.Summarize(_workspace, project);
        Assert.Equal(3, summary.TotalMessages);
        Assert.Equal(t0, summary.FirstMessageAt);
        Assert.Equal(t0.AddMinutes(10), summary.LastMessageAt);
        Assert.Equal(2, summary.CountsByKind["lifecycle"]);
        Assert.Equal(1, summary.CountsByKind["decision"]);
        Assert.Equal(1, summary.CountsByParticipant["orchestrator"]);
        Assert.Equal(2, summary.CountsByParticipant["runtime:taskboard"]);
        Assert.Equal(2, summary.CountsBySeverity["Info"]);
        Assert.Equal(1, summary.CountsBySeverity["Warn"]);
    }

    [Fact]
    public async Task GetById_ReturnsNullForUnknownId()
    {
        var store = new AgentMessageBusStore();
        await store.AppendAsync(_workspace, NewMessage("01HXYZ0000000000000000G001", "p"));
        Assert.Null(store.GetById(_workspace, "p", "does-not-exist"));
    }

    [Fact]
    public async Task HighVolume_TenThousandMessages_LoadsAndQueriesQuickly()
    {
        const int N = 10_000;
        var project = "agent-taskboard";
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pre-populate the file directly so the fixture cost is not in the
        // measured numbers; this is a load-and-query benchmark.
        var sw = Stopwatch.StartNew();
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                         bufferSize: 64 * 1024, useAsync: true))
        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            for (int i = 0; i < N; i++)
            {
                var id = "01HXYZ0000000000000000H" + i.ToString("D5");
                var m = NewMessage(id, project,
                    kind: i % 7 == 0 ? "decision" : "lifecycle",
                    participantId: i % 3 == 0 ? "agent:claude" : "runtime:taskboard",
                    jobId: "job-" + (i % 50),
                    runId: "run-" + (i % 200),
                    severity: i % 11 == 0 ? "Warn" : null,
                    createdAt: day.AddSeconds(i),
                    tags: i % 13 == 0 ? new[] { "perf" } : null);
                await writer.WriteLineAsync(JsonSerializer.Serialize(m, AgentMessageBusStore.SerializerOptions));
            }
        }
        sw.Stop();
        _out.WriteLine($"fixture-write: {sw.ElapsedMilliseconds} ms for {N} lines");

        var store = new AgentMessageBusStore();

        sw.Restart();
        var all = store.Recent(_workspace, project, limit: 1);
        sw.Stop();
        var loadMs = sw.ElapsedMilliseconds;
        _out.WriteLine($"cold-load+recent(1): {loadMs} ms for {N} messages");
        Assert.Single(all);

        sw.Restart();
        var byKind = store.Query(_workspace, project, new AgentMessageQuery(Kind: "decision"));
        sw.Stop();
        _out.WriteLine($"warm-query(kind=decision): {sw.ElapsedMilliseconds} ms returned {byKind.Count}");
        Assert.True(byKind.Count > 1000 && byKind.Count < N, $"unexpected count {byKind.Count}");

        sw.Restart();
        var byJob = store.Query(_workspace, project, new AgentMessageQuery(JobId: "job-7"));
        sw.Stop();
        _out.WriteLine($"warm-query(jobId=job-7): {sw.ElapsedMilliseconds} ms returned {byJob.Count}");

        sw.Restart();
        var summary = store.Summarize(_workspace, project);
        sw.Stop();
        _out.WriteLine($"warm-summary: {sw.ElapsedMilliseconds} ms total={summary.TotalMessages}");
        Assert.Equal(N, summary.TotalMessages);

        // Add a fresh append on top of the loaded projection; the new message
        // must show up in the next query without a reload.
        var newId = "01HXYZ0000000000000000Z999";
        await store.AppendAsync(_workspace, NewMessage(newId, project, createdAt: day.AddDays(1)));
        Assert.NotNull(store.GetById(_workspace, project, newId));
        Assert.Equal(N + 1, store.Summarize(_workspace, project).TotalMessages);

        // Loose acceptance bound: cold load of 10k small messages should not
        // approach the seconds range; we want to know if a future change
        // regresses by an order of magnitude.
        Assert.True(loadMs < 10_000, $"cold load took {loadMs} ms; well above the in-memory budget");
    }

    [Fact]
    public async Task AppendAsync_SerialisesConcurrentAppendsToSameDayFile()
    {
        var store = new AgentMessageBusStore();
        var project = "agent-taskboard";
        var day = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.AppendAsync(_workspace, NewMessage(
                "01HXYZ0000000000000000I" + i.ToString("D5"),
                project,
                createdAt: day.AddMilliseconds(i)))));

        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        var lines = File.ReadAllLines(path);
        Assert.Equal(50, lines.Length);
        // Each line must be parseable on its own (no interleaved writes).
        foreach (var line in lines)
        {
            var m = JsonSerializer.Deserialize<AgentMessage>(line, AgentMessageBusStore.SerializerOptions);
            Assert.NotNull(m);
        }
    }

    [Fact]
    public void Paths_AreCanonical()
    {
        var ws = Path.Combine("C:", "ws");
        Assert.Equal(Path.Combine(ws, "logs", "bus"), AgentMessageBusPaths.BusRoot(ws));
        Assert.Equal(Path.Combine(ws, "logs", "bus", "participants"), AgentMessageBusPaths.ParticipantsDir(ws));
        Assert.Equal(Path.Combine(ws, "logs", "bus", "participants", "user.json"),
            AgentMessageBusPaths.ParticipantFile(ws, "user"));
        Assert.Equal(Path.Combine(ws, "logs", "bus", "_workspace"),
            AgentMessageBusPaths.ProjectDir(ws, null));
        Assert.Equal(Path.Combine(ws, "logs", "bus", "agent-taskboard", "2026-05-05.jsonl"),
            AgentMessageBusPaths.DayFile(ws, "agent-taskboard",
                new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc)));
    }

    private static AgentMessage NewMessage(
        string id,
        string? project,
        string kind = "lifecycle",
        string role = "system",
        string participantId = "runtime:taskboard",
        string? severity = null,
        string? jobId = null,
        string? runId = null,
        string? correlationId = null,
        DateTime? createdAt = null,
        IReadOnlyList<string>? tags = null) => new()
    {
        Id = id,
        CreatedAt = createdAt ?? new DateTime(2026, 5, 5, 9, 0, 0, DateTimeKind.Utc),
        ParticipantId = participantId,
        Role = role,
        Kind = kind,
        Severity = severity,
        Project = project,
        JobId = jobId,
        RunId = runId,
        CorrelationId = correlationId,
        Summary = "test message " + id,
        Tags = tags,
    };
}
