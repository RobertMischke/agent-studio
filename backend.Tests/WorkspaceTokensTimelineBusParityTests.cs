using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase-5 parity test for <see cref="WorkspaceTokensTimelineService"/>.
/// Drives a realistic multi-project, multi-bucket data set through both
/// the legacy reader (<c>orchestrator.jsonl</c>) and the Phase-4
/// bus-backed reader (<see cref="BusBackedWorkspaceTimelineReader"/>)
/// and asserts byte-identical cells, per-project totals, and window
/// derivations.
///
/// <para>
/// The two readers can drift on bucket alignment, the
/// <c>AllModelsPriced</c> latch (which goes false when any call in the
/// bucket used an unpriced model), the per-project peak-bucket tracker,
/// and the <c>LastActivity</c> timestamp. The parity assertion compares
/// every field on every cell so any divergence on a real record fails
/// fast.
/// </para>
/// </summary>
public sealed class WorkspaceTokensTimelineBusParityTests : IDisposable
{
    private readonly string _workspace;
    private readonly Dictionary<string, string> _watchPaths;

    public WorkspaceTokensTimelineBusParityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "workspace-timeline-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _watchPaths = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Build_TwoProjectsMixedBuckets_24h_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var now = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

        // Two projects, mixed buckets, two priced models, one unpriced
        // sprinkled in so AllModelsPriced flips per bucket.
        await WriteAsync(log, bridge, store, "proj-alpha",
            MakeEntry("claude-opus-4-7",   80_000, 6_000, now.AddHours(-23)),
            MakeEntry("claude-opus-4-7",   20_000, 1_500, now.AddHours(-22).AddMinutes(15)),
            MakeEntry("claude-haiku-4-5", 100_000, 9_000, now.AddHours(-3).AddMinutes(45)),
            MakeEntry("future-experimental",  100,    10, now.AddHours(-3).AddMinutes(50)),
            MakeEntry("claude-haiku-4-5",   5_000,   500, now.AddHours(-1).AddMinutes(15)));
        await WriteAsync(log, bridge, store, "proj-beta",
            MakeEntry("claude-opus-4-7",   30_000, 2_000, now.AddHours(-12)),
            MakeEntry("claude-opus-4-7",   60_000, 5_000, now.AddHours(-12).AddMinutes(30)),
            MakeEntry("claude-haiku-4-5",  10_000,   900, now.AddHours(-2)));

        var projects = new[]
        {
            ("proj-alpha", _watchPaths["proj-alpha"]),
            ("proj-beta",  _watchPaths["proj-beta"]),
        };
        var legacy = WorkspaceTokensTimelineService.BuildFromEntries(
            new[]
            {
                ("proj-alpha", (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-alpha"])),
                ("proj-beta",  (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-beta"])),
            },
            windowStart: AlignDown(now, 60).AddHours(-24),
            windowEnd:   AlignDown(now, 60),
            bucketMinutes: 60);
        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(store, _workspace, projects, windowHours: 24, bucketMinutes: 60, nowUtc: now);

        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Build_OutOfWindowEntries_DroppedByBothReaders()
    {
        var (log, bridge, store) = BuildStack();
        var now = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);

        await WriteAsync(log, bridge, store, "proj-alpha",
            MakeEntry("claude-opus-4-7", 50_000, 4_000, now.AddDays(-7)),     // far outside 24h window
            MakeEntry("claude-opus-4-7", 70_000, 5_000, now.AddHours(-30)),   // just outside
            MakeEntry("claude-opus-4-7", 25_000, 2_000, now.AddHours(-5)));   // inside

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(store, _workspace, new[] { ("proj-alpha", _watchPaths["proj-alpha"]) }, windowHours: 24, bucketMinutes: 60, nowUtc: now);
        var legacy = WorkspaceTokensTimelineService.BuildFromEntries(
            new[] { ("proj-alpha", (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-alpha"])) },
            windowStart: AlignDown(now, 60).AddHours(-24),
            windowEnd:   AlignDown(now, 60),
            bucketMinutes: 60);

        AssertEquivalent(legacy, bus);
        Assert.Single(legacy.Cells); // only the inside entry survives
        Assert.Single(bus.Cells);
    }

    [Fact]
    public async Task Build_15MinBuckets_HighResolution_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var now = new DateTime(2026, 5, 11, 16, 0, 0, DateTimeKind.Utc);

        await WriteAsync(log, bridge, store, "proj-alpha",
            MakeEntry("claude-haiku-4-5", 5_000, 500, now.AddMinutes(-30)),
            MakeEntry("claude-haiku-4-5", 6_000, 600, now.AddMinutes(-29)),
            MakeEntry("claude-haiku-4-5", 7_000, 700, now.AddMinutes(-14)),
            MakeEntry("claude-haiku-4-5", 8_000, 800, now.AddMinutes(-1)));

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(store, _workspace, new[] { ("proj-alpha", _watchPaths["proj-alpha"]) }, windowHours: 1, bucketMinutes: 15, nowUtc: now);
        var legacy = WorkspaceTokensTimelineService.BuildFromEntries(
            new[] { ("proj-alpha", (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-alpha"])) },
            windowStart: AlignDown(now, 15).AddHours(-1),
            windowEnd:   AlignDown(now, 15),
            bucketMinutes: 15);

        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task Build_AllUnpriced_AllModelsPricedFalseAcrossCells()
    {
        var (log, bridge, store) = BuildStack();
        var now = new DateTime(2026, 5, 11, 8, 0, 0, DateTimeKind.Utc);

        await WriteAsync(log, bridge, store, "proj-alpha",
            MakeEntry("future-experimental", 1_000, 100, now.AddMinutes(-30)),
            MakeEntry("future-experimental", 2_000, 200, now.AddMinutes(-29)));

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(store, _workspace, new[] { ("proj-alpha", _watchPaths["proj-alpha"]) }, windowHours: 1, bucketMinutes: 60, nowUtc: now);
        var legacy = WorkspaceTokensTimelineService.BuildFromEntries(
            new[] { ("proj-alpha", (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-alpha"])) },
            windowStart: AlignDown(now, 60).AddHours(-1),
            windowEnd:   AlignDown(now, 60),
            bucketMinutes: 60);

        AssertEquivalent(legacy, bus);
        Assert.All(legacy.Cells, c => Assert.False(c.AllModelsPriced));
        Assert.All(bus.Cells, c => Assert.False(c.AllModelsPriced));
    }

    [Fact]
    public async Task Build_EmptyWindow_NoCellsOnEitherSide()
    {
        var (log, bridge, store) = BuildStack();
        var now = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

        // No entries at all for proj-alpha.
        _watchPaths["proj-alpha"] = Path.Combine(_workspace, "watched-proj-alpha");
        Directory.CreateDirectory(_watchPaths["proj-alpha"]);
        await Task.CompletedTask;

        var bus = BusBackedWorkspaceTimelineReader.BuildFromStore(store, _workspace, new[] { ("proj-alpha", _watchPaths["proj-alpha"]) }, windowHours: 24, bucketMinutes: 60, nowUtc: now);
        var legacy = WorkspaceTokensTimelineService.BuildFromEntries(
            new[] { ("proj-alpha", (IReadOnlyList<OrchestratorLogEntry>)log.Read(_watchPaths["proj-alpha"])) },
            windowStart: AlignDown(now, 60).AddHours(-24),
            windowEnd:   AlignDown(now, 60),
            bucketMinutes: 60);

        AssertEquivalent(legacy, bus);
        Assert.Empty(legacy.Cells);
        Assert.Empty(bus.Cells);
    }

    private static void AssertEquivalent(TokenTimeline a, TokenTimeline b)
    {
        Assert.Equal(a.WindowStart,    b.WindowStart);
        Assert.Equal(a.WindowEnd,      b.WindowEnd);
        Assert.Equal(a.WindowHours,    b.WindowHours);
        Assert.Equal(a.BucketMinutes,  b.BucketMinutes);
        Assert.Equal(a.BucketCount,    b.BucketCount);

        Assert.Equal(a.Cells.Count, b.Cells.Count);
        for (var i = 0; i < a.Cells.Count; i++)
        {
            Assert.Equal(a.Cells[i].Project,         b.Cells[i].Project);
            Assert.Equal(a.Cells[i].BucketStart,     b.Cells[i].BucketStart);
            Assert.Equal(a.Cells[i].BucketEnd,       b.Cells[i].BucketEnd);
            Assert.Equal(a.Cells[i].Calls,           b.Cells[i].Calls);
            Assert.Equal(a.Cells[i].Input,           b.Cells[i].Input);
            Assert.Equal(a.Cells[i].Output,          b.Cells[i].Output);
            Assert.Equal(a.Cells[i].CacheRead,       b.Cells[i].CacheRead);
            Assert.Equal(a.Cells[i].CacheWrite,      b.Cells[i].CacheWrite);
            Assert.Equal(a.Cells[i].Total,           b.Cells[i].Total);
            Assert.Equal(a.Cells[i].Dollars,         b.Cells[i].Dollars);
            Assert.Equal(a.Cells[i].AllModelsPriced, b.Cells[i].AllModelsPriced);
        }

        Assert.Equal(a.Projects.Count, b.Projects.Count);
        for (var i = 0; i < a.Projects.Count; i++)
        {
            Assert.Equal(a.Projects[i].Project,         b.Projects[i].Project);
            Assert.Equal(a.Projects[i].Calls,           b.Projects[i].Calls);
            Assert.Equal(a.Projects[i].Input,           b.Projects[i].Input);
            Assert.Equal(a.Projects[i].Output,          b.Projects[i].Output);
            Assert.Equal(a.Projects[i].CacheRead,       b.Projects[i].CacheRead);
            Assert.Equal(a.Projects[i].CacheWrite,      b.Projects[i].CacheWrite);
            Assert.Equal(a.Projects[i].Total,           b.Projects[i].Total);
            Assert.Equal(a.Projects[i].Dollars,         b.Projects[i].Dollars);
            Assert.Equal(a.Projects[i].AllModelsPriced, b.Projects[i].AllModelsPriced);
            Assert.Equal(a.Projects[i].PeakBucketStart, b.Projects[i].PeakBucketStart);
            Assert.Equal(a.Projects[i].PeakBucketTotal, b.Projects[i].PeakBucketTotal);
            Assert.Equal(a.Projects[i].LastActivity,    b.Projects[i].LastActivity);
        }
    }

    private (OrchestratorLog log, AgentMessageBusBridge bridge, AgentMessageBusStore store) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        return (log, bridge, store);
    }

    private async Task WriteAsync(
        OrchestratorLog log,
        AgentMessageBusBridge bridge,
        AgentMessageBusStore store,
        string projectName,
        params OrchestratorLogEntry[] entries)
    {
        if (!_watchPaths.ContainsKey(projectName))
        {
            var wp = Path.Combine(_workspace, "watched-" + projectName);
            Directory.CreateDirectory(wp);
            _watchPaths[projectName] = wp;
        }
        var watchPath = _watchPaths[projectName];
        foreach (var e in entries)
        {
            log.Append(watchPath, e);
            if (e.TokenUsage != null)
            {
                await bridge.EmitTokenUsageAsync(
                    project: projectName,
                    jobId: e.JobId,
                    participantId: AgentMessageBusBridge.ParticipantOrchestratorFor(projectName),
                    topic: e.Topic,
                    usage: e.TokenUsage,
                    createdAt: e.Ts);
            }
        }
        await WaitForBusCountAsync(store, projectName, entries.Count(x => x.TokenUsage != null));
    }

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, string projectName, int expected, int timeoutMs = 5_000)
    {
        var participant = AgentMessageBusBridge.ParticipantOrchestratorFor(projectName);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var got = store.Query(_workspace, projectName, new AgentMessageQuery(
                ParticipantId: participant,
                Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Bus did not reach {expected} {projectName} token-usage messages within {timeoutMs}ms.");
    }

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }

    private static OrchestratorLogEntry MakeEntry(string? model, int input, int output, DateTime ts, int cacheRead = 0, int cacheCreate = 0, string? jobId = null)
        => new()
        {
            Ts = ts,
            Kind = OrchestratorLogKinds.Decision,
            Topic = "orchestrator-decision",
            Summary = "parity entry",
            JobId = jobId,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheCreate,
            },
        };
}
