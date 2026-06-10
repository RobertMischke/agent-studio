using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the contract for session telemetry: every start / continue /
/// recovery must produce a row in <c>logs/session-events.jsonl</c>, the
/// captured session id must backfill the latest row after the run, and the
/// session chain in <c>task.json</c> must accumulate ids and break cleanly
/// at <c>(recovery)</c> markers.
///
/// The user explicitly asked for visibility into "was the session continued
/// or not". Without these tests, a refactor of <c>SetJobSessionName</c> /
/// <c>OnCliFinished</c> can silently drop the chain and the chip in the UI
/// would go quiet.
/// </summary>
public class SessionEventsTests : IDisposable
{
    private readonly string _watchPath;

    public SessionEventsTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private (TaskScannerService Scanner, TaskSessionLog Sessions) BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        return (scanner, sessions);
    }

    private void WriteJob(string state, string slug, string? sessionName = null, string[]? sessionChain = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var fields = new Dictionary<string, object?>
        {
            ["id"] = slug,
            ["title"] = slug,
            ["state"] = state,
            ["order"] = 1,
            ["agent"] = "claude"
        };
        if (sessionName != null) fields["sessionName"] = sessionName;
        if (sessionChain != null) fields["sessionChain"] = sessionChain;
        File.WriteAllText(Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void AppendSessionEvent_WritesJsonlRow()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var (_, sessions) = BuildServices();

        var ok = sessions.AppendSessionEvent("demo-task", new SessionEvent
        {
            Ts = new DateTime(2026, 5, 1, 14, 22, 10, DateTimeKind.Utc),
            Kind = "continue",
            Cli = "claude",
            InputSessionId = "abc-123",
            CapturedSessionId = null,
            Resumed = true,
            Reason = null
        }, _watchPath);

        Assert.True(ok);
        var events = sessions.ReadSessionEvents("demo-task", _watchPath);
        Assert.Single(events);
        Assert.Equal("continue", events[0].Kind);
        Assert.Equal("claude", events[0].Cli);
        Assert.Equal("abc-123", events[0].InputSessionId);
        Assert.True(events[0].Resumed);
    }

    [Fact]
    public void ReadSessionEvents_TolerantToTornLines()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var (_, sessions) = BuildServices();
        sessions.AppendSessionEvent("demo-task", new SessionEvent { Ts = DateTime.UtcNow, Kind = "start" }, _watchPath);

        // Inject a malformed trailing line — simulates a torn write.
        var path = Path.Combine(_watchPath, TaskStates.Progress, "demo-task", "logs", "session-events.jsonl");
        File.AppendAllText(path, "{not-valid-json" + Environment.NewLine);

        sessions.AppendSessionEvent("demo-task", new SessionEvent { Ts = DateTime.UtcNow, Kind = "continue" }, _watchPath);

        var events = sessions.ReadSessionEvents("demo-task", _watchPath);
        Assert.Equal(2, events.Count); // torn line was skipped
        Assert.Equal("start", events[0].Kind);
        Assert.Equal("continue", events[1].Kind);
    }

    [Fact]
    public void BackfillLatestSessionEventCapturedId_RewritesLastRow()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var (_, sessions) = BuildServices();
        sessions.AppendSessionEvent("demo-task", new SessionEvent { Ts = DateTime.UtcNow, Kind = "start", Cli = "claude" }, _watchPath);
        sessions.AppendSessionEvent("demo-task", new SessionEvent { Ts = DateTime.UtcNow, Kind = "continue", Cli = "claude", InputSessionId = "abc-123", Resumed = true }, _watchPath);

        var ok = sessions.BackfillLatestSessionEventCapturedId("demo-task", "def-456", _watchPath);

        Assert.True(ok);
        var events = sessions.ReadSessionEvents("demo-task", _watchPath);
        Assert.Equal(2, events.Count);
        Assert.Null(events[0].CapturedSessionId); // first event untouched
        Assert.Equal("def-456", events[1].CapturedSessionId);
        Assert.Equal("abc-123", events[1].InputSessionId);
    }

    [Fact]
    public void BackfillLatestSessionEventHeadShaRange_RewritesLatestRangeOnly()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var (_, sessions) = BuildServices();
        sessions.AppendSessionEvent("demo-task", new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-2),
            Kind = "start",
            Cli = "claude",
            HeadShaBefore = "old-before",
            HeadShaAfter = "old-after"
        }, _watchPath);
        sessions.AppendSessionEvent("demo-task", new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-1),
            Kind = "continue",
            Cli = "claude",
            HeadShaBefore = "spawn-before",
            HeadShaAfter = null
        }, _watchPath);

        var ok = sessions.BackfillLatestSessionEventHeadShaRange(
            "demo-task", "integration-base", "task-tip", _watchPath);

        Assert.True(ok);
        var events = sessions.ReadSessionEvents("demo-task", _watchPath);
        Assert.Equal("old-before", events[0].HeadShaBefore);
        Assert.Equal("old-after", events[0].HeadShaAfter);
        Assert.Equal("integration-base", events[1].HeadShaBefore);
        Assert.Equal("task-tip", events[1].HeadShaAfter);
    }

    [Fact]
    public void AppendSessionToChain_ExtendsChainAndUpdatesSessionName()
    {
        WriteJob(TaskStates.Progress, "demo-task", sessionName: "abc-123", sessionChain: ["abc-123"]);
        var (scanner, sessions) = BuildServices();

        sessions.AppendSessionToChain("demo-task", "def-456", _watchPath);

        var info = scanner.FindJob("demo-task", _watchPath)!;
        Assert.Equal(["abc-123", "def-456"], info.SessionChain);
        Assert.Equal("def-456", info.SessionName);
    }

    [Fact]
    public void AppendSessionToChain_IsIdempotentForSameTail()
    {
        WriteJob(TaskStates.Progress, "demo-task", sessionName: "abc-123", sessionChain: ["abc-123"]);
        var (scanner, sessions) = BuildServices();

        sessions.AppendSessionToChain("demo-task", "abc-123", _watchPath);

        var info = scanner.FindJob("demo-task", _watchPath)!;
        Assert.Equal(["abc-123"], info.SessionChain);
        Assert.Equal("abc-123", info.SessionName);
    }

    /// <summary>
    /// Recovery marker breaks the chain so the next captured id starts a new
    /// segment. The previous ids stay as history — they're the proof that
    /// the run did happen, even if the live session is gone.
    /// </summary>
    [Fact]
    public void MarkSessionChainRecovery_AppendsMarkerAndClearsCurrent()
    {
        WriteJob(TaskStates.Progress, "demo-task", sessionName: "abc-123", sessionChain: ["abc-123"]);
        var (scanner, sessions) = BuildServices();

        sessions.MarkSessionChainRecovery("demo-task", _watchPath);

        var info = scanner.FindJob("demo-task", _watchPath)!;
        Assert.Equal(["abc-123", "(recovery)"], info.SessionChain);
        // sessionName cleared so the next run mints fresh and AppendSessionToChain
        // doesn't dedupe it against the stale id.
        Assert.True(string.IsNullOrEmpty(info.SessionName));
    }

    [Fact]
    public void MarkSessionChainRecovery_NoOpOnEmptyChain()
    {
        // Brand-new job that's never started — there's nothing to mark a break on.
        WriteJob(TaskStates.Progress, "demo-task");
        var (scanner, sessions) = BuildServices();

        sessions.MarkSessionChainRecovery("demo-task", _watchPath);

        var info = scanner.FindJob("demo-task", _watchPath)!;
        Assert.Empty(info.SessionChain);
    }

    /// <summary>
    /// Job folders written before this PR shipped don't have <c>sessionChain</c>
    /// in <c>task.json</c>. The reader must derive a single-element chain from
    /// the legacy <c>sessionName</c> field so the UI shows continuity for old
    /// jobs instead of treating them as never-started.
    /// </summary>
    [Fact]
    public void SessionChain_ReadsLegacySessionNameAsSingleElement()
    {
        WriteJob(TaskStates.Progress, "legacy-task", sessionName: "legacy-uuid");
        var (scanner, _) = BuildServices();

        var info = scanner.FindJob("legacy-task", _watchPath)!;
        Assert.Equal(["legacy-uuid"], info.SessionChain);
    }

    [Fact]
    public void SessionChain_EmptyForBrandNewJob()
    {
        WriteJob(TaskStates.Progress, "fresh-task");
        var (scanner, _) = BuildServices();

        var info = scanner.FindJob("fresh-task", _watchPath)!;
        Assert.Empty(info.SessionChain);
    }

    /// <summary>
    /// The per-run passed-context snapshot is written to its own file under
    /// <c>logs/run-context/</c> (kept out of the polled events log) and the
    /// returned pointer must be a forward-slashed relative path under the job
    /// folder so the <c>/runs/{index}/context</c> endpoint can dereference it
    /// and the path-traversal guard can verify it stays inside the folder.
    /// </summary>
    [Fact]
    public void PersistRunContext_WritesFileAndReturnsRelativePointer()
    {
        WriteJob(TaskStates.Progress, "demo-task");
        var (scanner, sessions) = BuildServices();
        var folder = scanner.FindJob("demo-task", _watchPath)!.FolderPath;

        const string context = "PROMPT\n\n## Open items\n- carry these forward";
        var rel = sessions.PersistRunContext(folder, context);

        Assert.NotNull(rel);
        Assert.StartsWith("logs/run-context/", rel);
        Assert.DoesNotContain('\\', rel!); // forward-slashed for the endpoint guard

        var full = Path.Combine(folder, rel);
        Assert.True(File.Exists(full));
        Assert.Equal(context, File.ReadAllText(full));
    }
}
