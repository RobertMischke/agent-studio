using System.Text;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="InMemoryStore{T}"/> through its first two
/// concrete consumers, <see cref="SupervisorAdvisoryStore"/> and
/// <see cref="SupervisorInterventionStore"/>: append round-trips through disk,
/// reads survive malformed lines, the by-id lookup is stable, the cursor-based
/// reader returns only the new tail, and optimistic concurrency rejects a
/// stale write.
/// </summary>
public class InMemoryStoreTests : IDisposable
{
    private readonly string _workspace;

    public InMemoryStoreTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "in-memory-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Advisory_AppendAsync_PersistsToDiskAndIsReadableByFreshStore()
    {
        var store = new SupervisorAdvisoryStore();
        var advisory = NewAdvisory("agent-taskboard", "no-progress", SupervisorSeverity.Warn);

        var version = await store.AppendAsync(_workspace, advisory.Project, advisory);
        Assert.Equal(1, version);

        var path = SupervisorLogPaths.ObservationsFile(_workspace, advisory.Project);
        Assert.True(File.Exists(path));
        Assert.Single(File.ReadAllLines(path));

        var fresh = new SupervisorAdvisoryStore();
        var loaded = fresh.Snapshot(_workspace, advisory.Project);
        Assert.Single(loaded);
        Assert.Equal(advisory.Topic, loaded[0].Topic);
        Assert.Equal(advisory.Severity, loaded[0].Severity);
    }

    [Fact]
    public async Task Advisory_AppendAsync_RejectsRecordWithMissingRequiredFields()
    {
        var store = new SupervisorAdvisoryStore();
        var bad = new SupervisorAdvisory(
            CreatedAt: DateTime.UtcNow,
            Project: "agent-taskboard",
            Severity: SupervisorSeverity.Info,
            Source: SupervisorSource.HardCheck,
            Topic: "",
            Message: "non-empty");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(_workspace, bad.Project, bad));

        var path = SupervisorLogPaths.ObservationsFile(_workspace, bad.Project);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Advisory_LoadFromDisk_SkipsMalformedLinesWithoutBreakingProjection()
    {
        var project = "agent-taskboard";
        var dir = SupervisorLogPaths.ProjectLogDir(_workspace, project);
        Directory.CreateDirectory(dir);
        var path = SupervisorLogPaths.ObservationsFile(_workspace, project);

        var goodOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var good = JsonSerializer.Serialize(NewAdvisory(project, "quota-critical", SupervisorSeverity.High), goodOpts);

        File.WriteAllLines(path, new[]
        {
            "",
            "{not-json",
            "[1,2,3]",
            "{\"createdAt\":\"2026-05-05T10:00:00Z\",\"severity\":0,\"source\":0}", // missing project/topic/message
            good,
        }, Encoding.UTF8);

        var store = new SupervisorAdvisoryStore();
        var snap = store.Snapshot(_workspace, project);
        Assert.Single(snap);
        Assert.Equal("quota-critical", snap[0].Topic);
    }

    [Fact]
    public async Task Advisory_ReadSince_ReturnsOnlyNewTailAcrossSuccessiveCalls()
    {
        var store = new SupervisorAdvisoryStore();
        const string project = "agent-taskboard";

        await store.AppendAsync(_workspace, project, NewAdvisory(project, "topic-a"));
        await store.AppendAsync(_workspace, project, NewAdvisory(project, "topic-b"));

        var (firstBatch, cursor1) = store.ReadSince(_workspace, project, 0);
        Assert.Equal(2, firstBatch.Count);
        Assert.Equal(2, cursor1);

        var (emptyBatch, cursor2) = store.ReadSince(_workspace, project, cursor1);
        Assert.Empty(emptyBatch);
        Assert.Equal(cursor1, cursor2);

        await store.AppendAsync(_workspace, project, NewAdvisory(project, "topic-c"));
        var (tail, cursor3) = store.ReadSince(_workspace, project, cursor2);
        Assert.Single(tail);
        Assert.Equal("topic-c", tail[0].Topic);
        Assert.Equal(3, cursor3);
    }

    [Fact]
    public async Task Advisory_GetById_ReturnsTheRecordSynthesisedFromTimestampSourceAndTopic()
    {
        var store = new SupervisorAdvisoryStore();
        const string project = "agent-taskboard";
        var at = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var advisory = new SupervisorAdvisory(
            CreatedAt: at,
            Project: project,
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "stuck");

        await store.AppendAsync(_workspace, project, advisory);

        var id = string.Concat(project, "|", at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "|", SupervisorSource.HardCheck, "|", "no-progress");
        var found = store.GetById(_workspace, project, id);
        Assert.NotNull(found);
        Assert.Equal("stuck", found!.Message);

        Assert.Null(store.GetById(_workspace, project, "does-not-exist"));
    }

    [Fact]
    public async Task Advisory_AppendIfVersionAsync_RejectsStaleExpectedVersion()
    {
        var store = new SupervisorAdvisoryStore();
        const string project = "agent-taskboard";

        var v1 = await store.AppendAsync(_workspace, project, NewAdvisory(project, "first"));
        Assert.Equal(1, v1);

        // Concurrent writer advances the version behind our back.
        await store.AppendAsync(_workspace, project, NewAdvisory(project, "concurrent"));

        await Assert.ThrowsAsync<OptimisticConcurrencyException>(
            () => store.AppendIfVersionAsync(_workspace, project, NewAdvisory(project, "stale"), expectedVersion: v1));

        // Caller re-reads and retries with the current version.
        var current = store.GetVersion(_workspace, project);
        var v3 = await store.AppendIfVersionAsync(_workspace, project, NewAdvisory(project, "fresh"), current);
        Assert.Equal(current + 1, v3);
    }

    [Fact]
    public async Task Intervention_ValidatorRejectsCancelRunWithoutJobId()
    {
        var store = new SupervisorInterventionStore();
        var bad = new SupervisorIntervention(
            CreatedAt: DateTime.UtcNow,
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.CancelRun,
            Source: SupervisorSource.User,
            Reason: "needs jobId",
            JobId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(_workspace, bad.Project, bad));
    }

    [Fact]
    public async Task Intervention_RoundTripsPauseTtlThroughDisk()
    {
        var store = new SupervisorInterventionStore();
        const string project = "agent-taskboard";
        var original = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 5, 9, 0, 0, DateTimeKind.Utc),
            Project: project,
            Kind: SupervisorInterventionKind.PausePickup,
            Source: SupervisorSource.AutoIntervention,
            Reason: "investigating quota burn",
            PauseTtl: TimeSpan.FromMinutes(30));

        await store.AppendAsync(_workspace, project, original);

        var fresh = new SupervisorInterventionStore();
        var loaded = fresh.Snapshot(_workspace, project);
        Assert.Single(loaded);
        Assert.Equal(SupervisorInterventionKind.PausePickup, loaded[0].Kind);
        Assert.Equal(TimeSpan.FromMinutes(30), loaded[0].PauseTtl);
        Assert.Equal("investigating quota burn", loaded[0].Reason);
    }

    [Fact]
    public async Task Advisory_InvalidateProjection_ForcesNextReadFromDisk()
    {
        var store = new SupervisorAdvisoryStore();
        const string project = "agent-taskboard";

        await store.AppendAsync(_workspace, project, NewAdvisory(project, "topic-a"));
        Assert.Single(store.Snapshot(_workspace, project));

        // External writer appends a line that bypasses the store.
        var path = SupervisorLogPaths.ObservationsFile(_workspace, project);
        var extra = JsonSerializer.Serialize(NewAdvisory(project, "external"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.AppendAllText(path, extra + Environment.NewLine);

        // Without invalidation the projection still reflects the in-memory state.
        Assert.Single(store.Snapshot(_workspace, project));

        store.InvalidateProjection(_workspace, project);
        var refreshed = store.Snapshot(_workspace, project);
        Assert.Equal(2, refreshed.Count);
        Assert.Contains(refreshed, a => a.Topic == "external");
    }

    [Fact]
    public async Task Advisory_AppendAsync_SerialisesConcurrentAppendsToOneFile()
    {
        var store = new SupervisorAdvisoryStore();
        const string project = "agent-taskboard";

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            store.AppendAsync(_workspace, project,
                NewAdvisory(project, "topic-" + i, SupervisorSeverity.Info, atOffsetSeconds: i))));

        var path = SupervisorLogPaths.ObservationsFile(_workspace, project);
        var lines = File.ReadAllLines(path);
        Assert.Equal(50, lines.Length);

        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var line in lines)
        {
            var parsed = JsonSerializer.Deserialize<SupervisorAdvisory>(line, opts);
            Assert.NotNull(parsed);
        }
    }

    private static SupervisorAdvisory NewAdvisory(
        string project,
        string topic,
        SupervisorSeverity severity = SupervisorSeverity.Info,
        int atOffsetSeconds = 0) => new(
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc).AddSeconds(atOffsetSeconds),
            Project: project,
            Severity: severity,
            Source: SupervisorSource.HardCheck,
            Topic: topic,
            Message: "advisory for " + topic);
}
