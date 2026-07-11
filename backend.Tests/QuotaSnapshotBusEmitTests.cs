using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the AGT-2100 quota-snapshot event contract: the pure builder's
/// age/stale/window projection and the bus emission shape
/// (<c>kind:observation</c>, <c>topic:quota-snapshot</c>, compact
/// <c>QuotaSnapshotEvent</c> payload, one line of JSON per event). The
/// cap-forecast history depends on these stable field names, so the wiring
/// cannot silently regress.
/// </summary>
public sealed class QuotaSnapshotBusEmitTests : IDisposable
{
    // Pinned "now" so age/stale derivation in the emission tests is
    // deterministic regardless of the host wall clock.
    private static readonly DateTime Now = new(2026, 7, 11, 9, 0, 30, DateTimeKind.Utc);

    private readonly string _workspace;
    private readonly AgentMessageBusStore _store;
    private readonly AgentMessageBusBridge _bridge;

    public QuotaSnapshotBusEmitTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "quota-snap-bus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        _store = new AgentMessageBusStore();
        _bridge = new AgentMessageBusBridge(
            _store, config, NullLogger<AgentMessageBusBridge>.Instance, new FakeTimeProvider(Now));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static QuotaSnapshot SampleSnapshot(DateTime fetchedAt) => new()
    {
        CliType = "claude",
        FetchedAt = fetchedAt,
        Plan = "Max",
        Source = "/usage",
        Windows =
        [
            new QuotaWindow { Label = "5-hour", UsedPct = 42.5, ResetAt = fetchedAt.AddHours(3), Unit = "%" },
            new QuotaWindow { Label = "Weekly", UsedPct = 88, ResetAt = fetchedAt.AddDays(4), Unit = "%" },
        ],
    };

    [Fact]
    public void Build_FreshSnapshot_ProjectsAllWindowsAndAge()
    {
        var fetchedAt = new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc);
        var now = fetchedAt.AddSeconds(30);

        var evt = QuotaSnapshotEventBuilder.Build(
            QuotaSnapshotPhases.Start, "claude", "claude-opus-4-8", "high",
            SampleSnapshot(fetchedAt), TimeSpan.FromMinutes(10), now,
            runId: "job-1:637", jobId: "job-1");

        Assert.Equal("start", evt.Phase);
        Assert.Equal("claude", evt.CliType);
        Assert.Equal("claude-opus-4-8", evt.Model);
        Assert.Equal("high", evt.ThinkingLevel);
        Assert.Equal("Max", evt.Plan);
        Assert.Equal("job-1", evt.JobId);
        Assert.Equal(30, evt.SnapshotAgeSec);
        Assert.Equal(600, evt.TtlSeconds);
        Assert.False(evt.Stale);
        Assert.False(evt.Missing);
        Assert.Equal(2, evt.Windows.Count);
        Assert.Equal("5-hour", evt.Windows[0].Label);
        Assert.Equal(42.5, evt.Windows[0].UsedPct);
        Assert.Equal(fetchedAt.AddHours(3), evt.Windows[0].ResetAt);
        Assert.Equal("Weekly", evt.Windows[1].Label);
        Assert.Equal(88, evt.Windows[1].UsedPct);
    }

    [Fact]
    public void Build_OlderThanTtl_MarksStale()
    {
        var fetchedAt = new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc);
        var now = fetchedAt.AddSeconds(1200); // 20 min > 10 min TTL

        var evt = QuotaSnapshotEventBuilder.Build(
            QuotaSnapshotPhases.End, "claude", null, null,
            SampleSnapshot(fetchedAt), TimeSpan.FromMinutes(10), now);

        Assert.True(evt.Stale);
        Assert.Equal(1200, evt.SnapshotAgeSec);
        Assert.False(evt.Missing);
    }

    [Fact]
    public void Build_NullSnapshot_IsMissingWithNoWindows()
    {
        var evt = QuotaSnapshotEventBuilder.Build(
            QuotaSnapshotPhases.Start, "codex", "gpt-x", "medium",
            snapshot: null, TimeSpan.FromMinutes(10),
            new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc));

        Assert.True(evt.Missing);
        Assert.Empty(evt.Windows);
        Assert.Null(evt.FetchedAt);
        Assert.Null(evt.SnapshotAgeSec);
        Assert.Equal("codex", evt.CliType);
        Assert.Contains("no cached snapshot", QuotaSnapshotEventBuilder.Summarize(evt));
    }

    [Fact]
    public async Task Emit_WritesObservationWithQuotaSnapshotPayload()
    {
        var fetchedAt = new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc);

        await _bridge.EmitQuotaSnapshotAsync(
            project: "agent-taskboard",
            jobId: "AGT-2100",
            runId: "AGT-2100:637",
            cliType: "claude",
            model: "claude-opus-4-8",
            thinkingLevel: "high",
            phase: QuotaSnapshotPhases.Start,
            snapshot: SampleSnapshot(fetchedAt),
            ttl: TimeSpan.FromMinutes(10));

        var msg = Assert.Single(_store.Recent(_workspace, "agent-taskboard", 10));
        Assert.Equal("observation", msg.Kind);
        Assert.Equal("evidence", msg.Role);
        Assert.Equal("quota-snapshot", msg.Topic);
        Assert.Equal(AgentMessageBusBridge.ParticipantRuntime, msg.ParticipantId);
        Assert.Equal("AGT-2100", msg.JobId);
        Assert.Equal("AGT-2100:637", msg.RunId);
        Assert.NotNull(msg.Tags);
        Assert.Contains("quota-snapshot", msg.Tags!);
        Assert.Contains("phase:start", msg.Tags!);
        Assert.Contains("cli:claude", msg.Tags!);

        Assert.NotNull(msg.Payload);
        var payload = msg.Payload!.Value;
        Assert.Equal("start", payload.GetProperty("phase").GetString());
        Assert.Equal("claude", payload.GetProperty("cliType").GetString());
        Assert.Equal("claude-opus-4-8", payload.GetProperty("model").GetString());
        Assert.Equal("high", payload.GetProperty("thinkingLevel").GetString());
        Assert.False(payload.GetProperty("stale").GetBoolean());
        Assert.False(payload.GetProperty("missing").GetBoolean());
        var windows = payload.GetProperty("windows");
        Assert.Equal(2, windows.GetArrayLength());
        Assert.Equal("5-hour", windows[0].GetProperty("label").GetString());
        Assert.Equal(42.5, windows[0].GetProperty("usedPct").GetDouble());
    }

    [Fact]
    public async Task Emit_SerializesAsOneJsonLinePerEvent()
    {
        var fetchedAt = new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc);
        await _bridge.EmitQuotaSnapshotAsync(
            "agent-taskboard", "AGT-2100", "AGT-2100:637", "claude",
            "claude-opus-4-8", "high", QuotaSnapshotPhases.Start,
            SampleSnapshot(fetchedAt), TimeSpan.FromMinutes(10));
        await _bridge.EmitQuotaSnapshotAsync(
            "agent-taskboard", "AGT-2100", "AGT-2100:637", "claude",
            "claude-opus-4-8", "high", QuotaSnapshotPhases.End,
            SampleSnapshot(fetchedAt), TimeSpan.FromMinutes(10));

        // Both events land on the same UTC day-file, one JSON object per line.
        var dayFile = Directory
            .EnumerateFiles(Path.Combine(_workspace, "logs", "bus", "agent-taskboard"), "*.jsonl")
            .Single();
        var lines = (await File.ReadAllLinesAsync(dayFile))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            Assert.DoesNotContain('\n', line);
            using var doc = JsonDocument.Parse(line); // each line is standalone valid JSON
            Assert.Equal("observation", doc.RootElement.GetProperty("kind").GetString());
        }
    }
}
