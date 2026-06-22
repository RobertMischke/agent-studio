using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

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
    public void LoadFromDisk_SortsOutOfOrderLegacyFiles()
    {
        var project = "agent-taskboard";
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var messages = new[]
        {
            NewMessage("01HXYZ0000000000000000D003", project, createdAt: day.AddSeconds(3)),
            NewMessage("01HXYZ0000000000000000D001", project, createdAt: day.AddSeconds(1)),
            NewMessage("01HXYZ0000000000000000D002", project, createdAt: day.AddSeconds(2)),
        };
        File.WriteAllLines(
            path,
            messages.Select(m => JsonSerializer.Serialize(m, AgentMessageBusStore.SerializerOptions)),
            Encoding.UTF8);

        var store = new AgentMessageBusStore();
        var recent = store.Recent(_workspace, project, limit: 3);

        Assert.Equal(
            new[]
            {
                "01HXYZ0000000000000000D001",
                "01HXYZ0000000000000000D002",
                "01HXYZ0000000000000000D003",
            },
            recent.Select(m => m.Id).ToArray());
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

    /// <summary>
    /// Regression guard for the O(N^2) cold-load bug: <c>Projection.Append</c>
    /// cloned the whole message list + id-dict on every appended line, so
    /// replaying an N-line bus file was O(N^2). On the real Runbook bus
    /// (~100K lines) that was minutes of CPU + multi-GB transient garbage and
    /// wedged <c>/api/tasks</c> + <c>/api/tasks/grouped</c> (profiler showed every
    /// worker thread parked in Projection.Append -> Dictionary..ctor).
    ///
    /// The old <see cref="HighVolume_TenThousandMessages_LoadsAndQueriesQuickly"/>
    /// test missed it twice over: 10K is too small for the quadratic to bite,
    /// and a 10s threshold is loose enough that even the quadratic squeaked
    /// under it. This test pins the *shape*: doubling the message count must
    /// roughly double load time (linear), not quadruple it (quadratic), and a
    /// 50K-line cold load must finish in well under the time a quadratic load
    /// could ever achieve. Machine-independent: the ratio assertion holds
    /// regardless of CPU speed; the absolute bound is generous for O(N) yet
    /// unreachable for O(N^2) at this volume.
    /// </summary>
    [Fact]
    public void ColdLoad_ScalesLinearly_NotQuadratically()
    {
        // 25K vs 50K: large enough that O(N^2) is unmistakably slow (a 50K
        // quadratic replay is tens of seconds) while O(N) stays in the low
        // hundreds of ms, and the 2x doubling makes the scaling exponent
        // directly observable.
        var tSmall = MeasureColdLoadMs("scale-small", 25_000);
        var tLarge = MeasureColdLoadMs("scale-large", 50_000);
        _out.WriteLine($"cold-load 25K={tSmall} ms, 50K={tLarge} ms, ratio={(double)tLarge / Math.Max(1, tSmall):F2}");

        // Absolute bound: O(N) loads 50K small messages in the low hundreds of
        // ms; the pre-fix O(N^2) took tens of seconds. 3s cleanly separates the
        // two without being flaky on a slow CI box.
        Assert.True(tLarge < 3_000,
            $"50K-message cold load took {tLarge} ms — quadratic regression in Projection bulk load (expected well under 3s for O(N)).");

        // Shape bound: linear doubling is ~2x, quadratic is ~4x. Allow head-
        // room for filesystem cache, GC, and timer noise but stay below the
        // quadratic shape. The absolute 50K ceiling above is the primary guard
        // against the pre-fix per-append clone path.
        // Floor the denominator so a sub-millisecond small run can't blow up
        // the ratio on a very fast machine.
        var ratio = (double)tLarge / Math.Max(20, tSmall);
        Assert.True(ratio < 3.75,
            $"cold-load time grew {ratio:F2}x when message count doubled (25K->50K); "
            + "expected a linear-ish curve. A ratio near 4x means the O(N^2) per-append clone may be back.");
    }

    /// <summary>
    /// Writes <paramref name="count"/> bus messages straight to a day-file and
    /// returns the milliseconds a fresh <see cref="AgentMessageBusStore"/>
    /// takes to cold-load the projection (first query forces the disk replay).
    /// Each call uses its own project so projections never share cache state.
    /// </summary>
    private long MeasureColdLoadMs(string project, int count)
    {
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            for (int i = 0; i < count; i++)
            {
                var id = "01HXYZ00000000000000" + project[^1] + i.ToString("D9");
                var m = NewMessage(id, project,
                    kind: i % 7 == 0 ? "decision" : "lifecycle",
                    jobId: "job-" + (i % 50),
                    runId: "run-" + (i % 200),
                    createdAt: day.AddSeconds(i));
                writer.WriteLine(JsonSerializer.Serialize(m, AgentMessageBusStore.SerializerOptions));
            }
        }

        var store = new AgentMessageBusStore();
        var sw = Stopwatch.StartNew();
        var recent = store.Recent(_workspace, project, limit: 1); // forces cold load
        sw.Stop();
        Assert.Single(recent);
        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// Boot-time warmup contract: <c>WarmProject</c> must move the cold-load
    /// cost out of the first read so the post-restart UpdateVerifier window
    /// finds a hot projection. A project that was explicitly warmed must
    /// serve subsequent reads without touching disk again, and a second
    /// warmup must be a hot dictionary lookup.
    ///
    /// <para>Regression context: with no warmup, the first
    /// <c>/api/tasks/grouped</c> after a backend restart triggers the
    /// disk replay of every per-day JSONL inside <c>GetOrLoad</c>. On a
    /// real workspace (Runbook = ~100MB / >100k lines) that load runs
    /// longer than the verifier's 10s per-attempt timeout, and because
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,System.Func{TKey,TValue})"/>
    /// does not memoise a factory that threw under cancellation, every
    /// retried HTTP call restarts the disk replay from scratch. The
    /// projection therefore never finishes loading and the operator sees
    /// "no response (timed out or unreachable)". Paying the parse cost
    /// during <c>WarmProject</c> at boot eliminates that loop entirely.</para>
    /// </summary>
    [Fact]
    public void WarmProject_LoadsProjection_AndSubsequentReadsAreHot()
    {
        var project = "warmup-target";
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        const int N = 5_000;
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            for (int i = 0; i < N; i++)
            {
                var id = "01HXYZ0000000000000000W" + i.ToString("D5");
                writer.WriteLine(JsonSerializer.Serialize(
                    NewMessage(id, project, kind: "token-usage", jobId: "job-" + (i % 25), createdAt: day.AddSeconds(i)),
                    AgentMessageBusStore.SerializerOptions));
            }
        }

        var store = new AgentMessageBusStore();

        var warmSw = Stopwatch.StartNew();
        var warmedCount = store.WarmProject(_workspace, project);
        warmSw.Stop();
        Assert.Equal(N, warmedCount);
        _out.WriteLine($"WarmProject: {warmSw.ElapsedMilliseconds} ms for {N} messages");

        // First read after warmup must be cheap — no second disk walk.
        // We allow a generous absolute ceiling and assert the warm read
        // is much faster than the warmup itself. The Max-floor on the
        // denominator keeps the ratio test honest on fast machines where
        // the warmup itself completed in a handful of ms.
        var hotSw = Stopwatch.StartNew();
        var recent = store.Recent(_workspace, project, limit: 1);
        hotSw.Stop();
        Assert.Single(recent);
        _out.WriteLine($"post-warm Recent(1): {hotSw.ElapsedMilliseconds} ms");
        Assert.True(hotSw.ElapsedMilliseconds < Math.Max(50, warmSw.ElapsedMilliseconds / 5),
            $"post-warm read took {hotSw.ElapsedMilliseconds} ms vs warmup {warmSw.ElapsedMilliseconds} ms — "
            + "WarmProject must populate the projection cache so subsequent reads do not re-walk disk.");

        var secondWarmSw = Stopwatch.StartNew();
        var secondCount = store.WarmProject(_workspace, project);
        secondWarmSw.Stop();
        Assert.Equal(N, secondCount);
        _out.WriteLine($"WarmProject (second call): {secondWarmSw.ElapsedMilliseconds} ms");
        Assert.True(secondWarmSw.ElapsedMilliseconds < Math.Max(50, warmSw.ElapsedMilliseconds / 5),
            $"second WarmProject took {secondWarmSw.ElapsedMilliseconds} ms — should be a hot dictionary lookup.");
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

    /// <summary>
    /// Regression guard for the post-restart hang documented in
    /// "Update-Service has had 0/20 successful runs since 2026-05-23": the
    /// first <c>/api/tasks/grouped</c> call after a backend restart kicked off
    /// a multi-megabyte JSONL replay on the request thread, the verifier
    /// cancelled at 10s, and because <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
    /// does not cache a throwing factory, each retry restarted the load from
    /// scratch. <see cref="AgentMessageBusStore.WarmProject"/> is the boot-time
    /// hook Program.cs uses to move the parse cost out of the request path;
    /// this test pins that, after WarmProject runs, subsequent reads are
    /// served from the in-memory projection with no further disk I/O.
    /// </summary>
    [Fact]
    public void WarmProject_LoadsProjectionUpFront_SubsequentReadsAreFromMemory()
    {
        const int N = 2_000;
        var project = "warmup-target";
        var day = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var path = AgentMessageBusPaths.DayFile(_workspace, project, day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            for (int i = 0; i < N; i++)
            {
                var id = "01HXYZ0000000000000000W" + i.ToString("D5");
                var m = NewMessage(id, project,
                    kind: i % 3 == 0 ? "token-usage" : "lifecycle",
                    jobId: "job-" + (i % 20),
                    createdAt: day.AddSeconds(i));
                writer.WriteLine(JsonSerializer.Serialize(m, AgentMessageBusStore.SerializerOptions));
            }
        }

        var store = new AgentMessageBusStore();
        var warmSw = Stopwatch.StartNew();
        var loaded = store.WarmProject(_workspace, project);
        warmSw.Stop();
        _out.WriteLine($"WarmProject: {warmSw.ElapsedMilliseconds} ms loaded={loaded}");
        Assert.Equal(N, loaded);

        // After warmup, a Recent / Query / Summarize must be served from
        // memory in single-digit ms. Three orders of magnitude headroom over
        // the cold load lets the assertion survive a slow CI box without
        // letting a regression that re-introduces per-request disk I/O slip
        // past.
        var readSw = Stopwatch.StartNew();
        var recent = store.Recent(_workspace, project, limit: 1);
        var byKind = store.Query(_workspace, project, new AgentMessageQuery(Kind: "token-usage"));
        var summary = store.Summarize(_workspace, project);
        readSw.Stop();
        _out.WriteLine($"post-warmup-reads: {readSw.ElapsedMilliseconds} ms");
        Assert.Single(recent);
        Assert.True(byKind.Count > 0);
        Assert.Equal(N, summary.TotalMessages);
        Assert.True(readSw.ElapsedMilliseconds < Math.Max(500, warmSw.ElapsedMilliseconds),
            $"Post-warmup reads took {readSw.ElapsedMilliseconds} ms; the projection should be in memory now. "
            + $"WarmProject took {warmSw.ElapsedMilliseconds} ms — a second comparable cost means the cache slot was lost.");
    }

    [Fact]
    public void WarmProject_RejectsEmptyArguments()
    {
        var store = new AgentMessageBusStore();
        Assert.Throws<ArgumentException>(() => store.WarmProject("", "p"));
        Assert.Throws<ArgumentException>(() => store.WarmProject(_workspace, ""));
    }

    [Fact]
    public void WarmProject_OnUnseenProject_ReturnsZeroAndCachesEmpty()
    {
        // A project with no on-disk bus folder must still cache an empty
        // projection so the next Query returns instantly instead of redoing
        // the (no-op) disk scan on every request.
        var store = new AgentMessageBusStore();
        var n = store.WarmProject(_workspace, "no-bus-here");
        Assert.Equal(0, n);
        var recent = store.Recent(_workspace, "no-bus-here", limit: 1);
        Assert.Empty(recent);
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
