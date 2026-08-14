using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase-5 parity test for ad-hoc Haiku usage. Drives the recorder through
/// realistic records (mixed sources, mixed models, span multiple UTC days,
/// includes one unpriced model so the <c>AllModelsPriced=false</c> branch
/// is exercised) and asserts the legacy <c>adhoc-usage.jsonl</c> reader and
/// the bus-backed reader produce byte-identical aggregates.
///
/// <para>
/// This is the regression guard for Phase 4 of the token-aggregation
/// consolidation (docs/system/domains/tokens.md). The read paths can diverge in
/// subtle ways: ordering of <c>BySource</c> buckets, dollar quantisation,
/// model-key casing, day-key formatting, the (unknown)-model fallback. The
/// parity assertion compares every numeric field plus the ordered lists, so
/// any divergence on a real record fails fast.
/// </para>
/// </summary>
public sealed class AdHocUsageBusParityTests : IDisposable
{
    private readonly string _workspace;

    public AdHocUsageBusParityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "adhoc-parity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    // MachineBound 19.07.: Bus-Drain-Timing flakt unter Parallellast im Karten-Gate (Analyse-Vorbehalt s.u. bleibt bestehen).
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task LegacyAndBusReaders_ProduceIdenticalAggregateForMixedRealisticRecords()
    {
        var (recorder, store, _) = BuildStack();

        var day1 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 5, 2, 14, 0, 0, DateTimeKind.Utc);
        var day3 = new DateTime(2026, 5, 3, 9, 30, 0, DateTimeKind.Utc);

        // Realistic mix: every active named source, two days, a priced model + an
        // unpriced one so AllModelsPriced=false on both sides, a project-scoped
        // entry and a workspace-only entry, a CommitMessage record with a
        // jobId (the GitService path) and a TitleGeneration record without.
        var records = new[]
        {
            Make(AdHocUsageSources.TitleGeneration,   "claude-haiku-4-5",            input: 1500, output: 80,   day1),
            Make(AdHocUsageSources.TitleGeneration,   "claude-haiku-4-5",            input: 2200, output: 110,  day1.AddMinutes(5)),
            Make(AdHocUsageSources.SummaryGeneration, "claude-haiku-4-5",            input: 48_000, output: 950, day1.AddHours(2)),
            Make(AdHocUsageSources.PromptEnhancement, "claude-haiku-4-5",            input: 900, output: 60,    day2),
            Make(AdHocUsageSources.CommitMessage,     "claude-haiku-4-5",            input: 6_000, output: 320, day2.AddHours(2), jobId: "some-job"),
            Make(AdHocUsageSources.SoftReasoning,     "claude-haiku-4-5",            input: 3_200, output: 180, day3),
            Make(AdHocUsageSources.ReviewDecision,    "claude-haiku-4-5",            input: 12_000, output: 420, day3.AddMinutes(15), project: "agent-taskboard"),
            // Unpriced model so AllModelsPriced=false on both sides.
            Make(AdHocUsageSources.TitleGeneration,   "claude-experimental-future",  input: 50, output: 5,      day3.AddHours(3)),
        };

        // Write through the recorder. Each Record() call writes JSONL
        // synchronously and fires a fire-and-forget bus emit.
        foreach (var r in records)
        {
            Assert.True(recorder.Record(r));
        }

        // Wait for the bus side to catch up. Record() mirrors onto the bus as
        // fire-and-forget (the returned Task is discarded by design - the bus is
        // observability and must not block the canonical JSONL write), so the
        // only completion signal available is the message count. The emits are
        // durable, never dropped; they just queue behind other work when the
        // threadpool is saturated by sibling parallel test collections. A 5s
        // budget starved to 3/8 under that load, so the window is generous
        // (30s) rather than tight - it drains in ~140ms unloaded.
        await WaitForBusCountAsync(store, expected: records.Length, timeoutMs: 30_000);

        var legacy = AdHocUsageService.Aggregate(recorder.ReadAll(), recorder.LogPath, 0, null);
        var bus    = BusBackedAdHocUsageReader.AggregateFromStore(store, _workspace);

        AssertEquivalent(legacy, bus);
    }

    [Fact]
    public async Task EmptyState_BothReadersReturnEmptyAggregate()
    {
        var (_, store, _) = BuildStack();
        // Bus has no records, JSONL file does not exist.
        await Task.CompletedTask;

        var legacy = AdHocUsageService.Aggregate(Array.Empty<AdHocUsageRecord>(), "(none)", 0, null);
        var bus    = BusBackedAdHocUsageReader.AggregateFromStore(store, _workspace);

        Assert.Equal(0, legacy.Calls);
        Assert.Equal(0, bus.Calls);
        Assert.Empty(legacy.BySource);
        Assert.Empty(bus.BySource);
    }

    /// <summary>
    /// Compares two <see cref="AdHocUsageAggregate"/>s on every numeric field
    /// and on the contents (and ordering) of every breakdown list. The two
    /// "free" differences (<c>LogPath</c>, <c>LogModifiedAt</c>, <c>LogSizeBytes</c>)
    /// are excluded because the JSONL reader points at a real file and the bus
    /// reader does not; those fields are presentation-only.
    /// </summary>
    private static void AssertEquivalent(AdHocUsageAggregate a, AdHocUsageAggregate b)
    {
        Assert.Equal(a.Calls,                  b.Calls);
        Assert.Equal(a.InputTokens,            b.InputTokens);
        Assert.Equal(a.OutputTokens,           b.OutputTokens);
        Assert.Equal(a.CacheReadTokens,        b.CacheReadTokens);
        Assert.Equal(a.CacheCreationTokens,    b.CacheCreationTokens);
        Assert.Equal(a.EstimatedApiCostUsd,    b.EstimatedApiCostUsd);
        Assert.Equal(a.AllModelsPriced,        b.AllModelsPriced);

        AssertSourceListEqual(a.BySource, b.BySource);
        AssertDayListEqual(a.ByDay,       b.ByDay);
        AssertModelListEqual(a.ByModel,   b.ByModel);
    }

    private static void AssertSourceListEqual(IReadOnlyList<AdHocUsageBySource> x, IReadOnlyList<AdHocUsageBySource> y)
    {
        Assert.Equal(x.Count, y.Count);
        for (var i = 0; i < x.Count; i++)
        {
            Assert.Equal(x[i].Source,                y[i].Source);
            Assert.Equal(x[i].Calls,                 y[i].Calls);
            Assert.Equal(x[i].InputTokens,           y[i].InputTokens);
            Assert.Equal(x[i].OutputTokens,          y[i].OutputTokens);
            Assert.Equal(x[i].CacheReadTokens,       y[i].CacheReadTokens);
            Assert.Equal(x[i].CacheCreationTokens,   y[i].CacheCreationTokens);
            Assert.Equal(x[i].EstimatedApiCostUsd,   y[i].EstimatedApiCostUsd);
        }
    }

    private static void AssertDayListEqual(IReadOnlyList<AdHocUsageByDay> x, IReadOnlyList<AdHocUsageByDay> y)
    {
        Assert.Equal(x.Count, y.Count);
        for (var i = 0; i < x.Count; i++)
        {
            Assert.Equal(x[i].Date,                  y[i].Date);
            Assert.Equal(x[i].Calls,                 y[i].Calls);
            Assert.Equal(x[i].InputTokens,           y[i].InputTokens);
            Assert.Equal(x[i].OutputTokens,          y[i].OutputTokens);
            Assert.Equal(x[i].CacheReadTokens,       y[i].CacheReadTokens);
            Assert.Equal(x[i].CacheCreationTokens,   y[i].CacheCreationTokens);
            Assert.Equal(x[i].EstimatedApiCostUsd,   y[i].EstimatedApiCostUsd);
        }
    }

    private static void AssertModelListEqual(IReadOnlyList<AdHocUsageByModel> x, IReadOnlyList<AdHocUsageByModel> y)
    {
        Assert.Equal(x.Count, y.Count);
        for (var i = 0; i < x.Count; i++)
        {
            Assert.Equal(x[i].Model,                 y[i].Model);
            Assert.Equal(x[i].Calls,                 y[i].Calls);
            Assert.Equal(x[i].InputTokens,           y[i].InputTokens);
            Assert.Equal(x[i].OutputTokens,          y[i].OutputTokens);
            Assert.Equal(x[i].CacheReadTokens,       y[i].CacheReadTokens);
            Assert.Equal(x[i].CacheCreationTokens,   y[i].CacheCreationTokens);
            Assert.Equal(x[i].EstimatedApiCostUsd,   y[i].EstimatedApiCostUsd);
            Assert.Equal(x[i].ModelPriced,           y[i].ModelPriced);
            Assert.Equal(x[i].OldestEntryAt,         y[i].OldestEntryAt);
            Assert.Equal(x[i].NewestEntryAt,         y[i].NewestEntryAt);
        }
    }

    private (AdHocUsageRecorder recorder, AgentMessageBusStore store, AgentMessageBusBridge bridge) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        var recorder = new AdHocUsageRecorder(NullLogger<AdHocUsageRecorder>.Instance, config, bridge);
        return (recorder, store, bridge);
    }

    private static AdHocUsageRecord Make(
        string source,
        string model,
        int input,
        int output,
        DateTime ts,
        int cacheRead = 0,
        int cacheCreate = 0,
        long durationMs = 200,
        bool ok = true,
        string? project = null,
        string? jobId = null) => new()
    {
        Ts = ts,
        Source = source,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheCreate,
        DurationMs = durationMs,
        Ok = ok,
        Project = project,
        JobId = jobId,
    };

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, int expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var got = store.Query(_workspace, AgentMessageBusPaths.WorkspaceScope, new AgentMessageQuery(
                ParticipantId: "support:adhoc",
                Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        var final = store.Query(_workspace, AgentMessageBusPaths.WorkspaceScope, new AgentMessageQuery(
            ParticipantId: "support:adhoc",
            Kind: "token-usage")).Count;
        Assert.Fail($"Bus did not reach {expected} support:adhoc token-usage messages within {timeoutMs}ms (got {final}).");
    }
}
