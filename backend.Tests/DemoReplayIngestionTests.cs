using System.Text.Json;
using AgentStudio.DemoReplay;
using AgentStudio.Diagnostics;
using AgentStudio.Persistence;
using AgentStudio.Projection;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The write side of the replay plane. These tests pin the two properties the
/// slice promises about content: an admitted step is materialized from the
/// server's own trace copy, and every row it writes is labeled simulated.
/// </summary>
public class DemoReplayIngestionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "demo-replay-ingest-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero));

    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found above the test base directory.");
    }

    internal static string CommittedTracePath()
        => Path.Combine(RepoRoot(), "testdata", "demo-replay", "demo-replay-trace.json");

    private sealed class StubScanner(IReadOnlyList<TaskInfo> tasks) : ITaskScanner
    {
        public List<TaskInfo> ScanAllJobs() => [.. tasks];
    }

    private (DemoReplayIngestionService Replay, DemoReplayOptions Options) Build(params string[] taskKeys)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoReplay:Enabled"] = "true",
            ["DemoReplay:TracePath"] = CommittedTracePath(),
            ["DemoReplay:SigningKey"] = "demo-replay-development-key",
        }).Build();

        var options = DemoReplayOptions.Load(configuration);
        var tasks = taskKeys
            .Select(key => new TaskInfo { Id = key, Key = key, TaskKey = key, FolderPath = Path.Combine(_root, key) })
            .ToList();
        var state = new DemoReplayPlaneState(_time);
        var service = new DemoReplayIngestionService(
            options, state, new StubScanner(tasks), new RunnerEventJournal(new JsonlAppender()));
        return (service, options);
    }

    private DemoReplayEventRequest Cursor(DemoReplayOptions options, long epoch, int sequence)
        => new(options.Trace!.TraceId, options.TraceDigest, epoch, sequence);

    /// <summary>
    /// The signing key in configuration exercises the authenticity branch, so
    /// this also proves the committed fixture was signed with the documented
    /// development key.
    /// </summary>
    [Fact]
    public void The_committed_trace_passes_startup_verification()
    {
        var (_, options) = Build();

        Assert.True(options.Enabled);
        Assert.Equal(720, options.Trace!.CycleSeconds);
        Assert.Equal(360, options.MinEpochSeconds);
        Assert.Equal(options.Trace.Signature!.Digest, options.TraceDigest);
    }

    [Fact]
    public async Task An_admitted_step_writes_one_simulated_journal_row()
    {
        var (replay, options) = Build("DEMO-4");

        var result = await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Event!.Simulated);
        Assert.Equal("DEMO-4", result.Event.TaskKey);

        var recorded = RunnerEventSource.ReadRecords(new TaskInfo { Id = "DEMO-4", FolderPath = Path.Combine(_root, "DEMO-4") });
        var row = Assert.Single(recorded);
        Assert.True(row.Simulated);
        Assert.Equal("session.started", row.Kind);
        Assert.Equal(options.Trace!.Events[0].Message, row.Message);
    }

    /// <summary>
    /// The on-disk row must carry the flag, not only the in-memory projection,
    /// because the frontend reads the journal through the projection cache.
    /// </summary>
    [Fact]
    public async Task The_persisted_row_carries_the_simulated_flag()
    {
        var (replay, options) = Build("DEMO-4");
        await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);

        var line = File.ReadAllLines(Path.Combine(_root, "DEMO-4", "logs", "runner-events.jsonl")).Single();

        using var document = JsonDocument.Parse(line);
        Assert.True(document.RootElement.GetProperty("simulated").GetBoolean());
    }

    /// <summary>
    /// The request carries no content, so the only way to reach a task is to
    /// name a step the trace already assigns to it. There is no field an
    /// attacker could point somewhere else.
    /// </summary>
    [Fact]
    public async Task Steps_land_only_on_the_task_the_trace_names()
    {
        var (replay, options) = Build("DEMO-4", "DEMO-12");

        await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);
        await replay.IngestAsync(Cursor(options, 1, 2), CancellationToken.None);
        var third = await replay.IngestAsync(Cursor(options, 1, 3), CancellationToken.None);

        Assert.True(third.Accepted);
        Assert.Equal("DEMO-12", third.Event!.TaskKey);
        Assert.Equal(2, RunnerEventSource.ReadRecords(new TaskInfo { Id = "DEMO-4", FolderPath = Path.Combine(_root, "DEMO-4") }).Count);
        Assert.Single(RunnerEventSource.ReadRecords(new TaskInfo { Id = "DEMO-12", FolderPath = Path.Combine(_root, "DEMO-12") }));
    }

    [Fact]
    public async Task Resending_an_admitted_step_writes_nothing()
    {
        var (replay, options) = Build("DEMO-4");
        await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);

        var repeat = await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);

        Assert.False(repeat.Accepted);
        Assert.Equal(DemoReplayDenials.SequenceOutOfOrder, repeat.Reason);
        Assert.Single(RunnerEventSource.ReadRecords(new TaskInfo { Id = "DEMO-4", FolderPath = Path.Combine(_root, "DEMO-4") }));
    }

    /// <summary>A task the seed does not contain must not create a folder outside the declared scene.</summary>
    [Fact]
    public async Task A_step_whose_task_is_absent_from_the_datastore_writes_nothing()
    {
        var (replay, options) = Build();

        var result = await replay.IngestAsync(Cursor(options, 1, 1), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(DemoReplayDenials.SceneTaskMissing, result.Reason);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task The_rate_window_bounds_a_burst_and_reopens_after_it_passes()
    {
        var (replay, options) = Build("DEMO-4", "DEMO-12");
        var admitted = 0;
        for (var sequence = 1; sequence <= options.MaxEventsPerWindow + 1; sequence++)
        {
            if ((await replay.IngestAsync(Cursor(options, 1, sequence), CancellationToken.None)).Accepted) admitted++;
        }

        Assert.Equal(options.MaxEventsPerWindow, admitted);

        _time.Advance(TimeSpan.FromSeconds(DemoReplayOptions.RateWindowSeconds + 1));
        var afterWindow = await replay.IngestAsync(Cursor(options, 1, options.MaxEventsPerWindow + 1), CancellationToken.None);

        Assert.True(afterWindow.Accepted);
    }

    [Fact]
    public void The_read_only_projection_always_reports_simulated()
    {
        var (replay, options) = Build("DEMO-4");

        var projection = replay.Projection();

        Assert.True(projection.Simulated);
        Assert.Equal(options.Trace!.TraceId, projection.TraceId);
        Assert.Equal(0, projection.LastSequence);
        Assert.Equal(options.Trace.Events.Count, projection.EventCount);
    }

    [Fact]
    public void A_missing_trace_file_fails_startup_instead_of_disabling_the_plane()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoReplay:Enabled"] = "true",
            ["DemoReplay:TracePath"] = Path.Combine(_root, "absent.json"),
        }).Build();

        Assert.Throws<InvalidOperationException>(() => DemoReplayOptions.Load(configuration));
    }

    [Fact]
    public void A_pinned_digest_that_does_not_match_the_bundle_fails_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoReplay:Enabled"] = "true",
            ["DemoReplay:TracePath"] = CommittedTracePath(),
            ["DemoReplay:TraceDigest"] = new string('c', 64),
        }).Build();

        Assert.Throws<InvalidOperationException>(() => DemoReplayOptions.Load(configuration));
    }

    [Fact]
    public void A_signing_key_the_bundle_did_not_use_fails_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoReplay:Enabled"] = "true",
            ["DemoReplay:TracePath"] = CommittedTracePath(),
            ["DemoReplay:SigningKey"] = "attacker-key",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => DemoReplayOptions.Load(configuration));
    }

    [Fact]
    public void The_plane_is_off_unless_it_is_explicitly_enabled()
    {
        var options = DemoReplayOptions.Load(new ConfigurationBuilder().Build());

        Assert.False(options.Enabled);
        Assert.Null(options.Trace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
