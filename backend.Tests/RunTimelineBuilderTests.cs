using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the pure aggregation that turns session-events.jsonl + cli-output.log
/// into the run timeline that drives the protocol-pane redesign. The
/// builder is the only piece that touches both data sources, so a regression
/// here breaks the entire run-list UI; pinning the matrix keeps the
/// drift-prone parts (line-spans, status pairing, user-followup capture)
/// honest.
/// </summary>
public class RunTimelineBuilderTests
{
    private static readonly DateTime T0 = new(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc);

    private static CliOutputLine Sys(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "system", Text = text };

    private static CliOutputLine User(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "user", Text = text };

    private static CliOutputLine StdOut(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "stdout", Text = text };

    [Fact]
    public void EmptyInputs_ReturnEmptyTimeline()
    {
        var t = RunTimelineBuilder.Build(events: [], lines: [], nowUtc: T0);
        Assert.Equal(0, t.RunCount);
        Assert.Empty(t.Runs);
        Assert.Null(t.FirstStartedAt);
        Assert.Null(t.LastActivityAt);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void SingleCompletedRun_PairsEventWithStartedAndExitedMarkers()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude", Resumed = false, CapturedSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(2, "Hello"),
            Sys(60, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=58.4s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(120));

        Assert.Equal(1, t.RunCount);
        Assert.True(t.FirstStartedAt.HasValue);
        var r = Assert.Single(t.Runs);
        Assert.Equal(1, r.Index);
        Assert.Equal("start", r.Intent);
        Assert.Equal("completed", r.Status);
        Assert.Equal("claude", r.Cli);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(58.4, r.DurationSeconds);
        Assert.Equal("uuid-1", r.CapturedSessionId);
        Assert.Equal(1, r.LineStart);
        Assert.Equal(3, r.LineEnd);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void RunningRun_HasNullEndAndRunningStatus()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(2, "Working")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(30));

        var r = Assert.Single(t.Runs);
        Assert.Equal("running", r.Status);
        Assert.Null(r.EndedAt);
        Assert.Null(r.ExitCode);
        Assert.True(t.HasActiveRun);
        Assert.Equal(1, r.LineStart);
        Assert.Equal(2, r.LineEnd);
    }

    [Fact]
    public void TwoRuns_UserFollowupCapturedFromBetweenLines()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude" },
            new() { Ts = T0.AddSeconds(120), Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(10, "first run output"),
            Sys(50, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=50s"),
            User(110, "Keep going and add tests"),
            Sys(120, "[taskboard] Started claude CLI (PID 1235)"),
            StdOut(125, "ok"),
            Sys(180, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=60s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(200));

        Assert.Equal(2, t.RunCount);

        Assert.Null(t.Runs[0].UserFollowup); // fresh start - no preceding user line
        Assert.Equal("Keep going and add tests", t.Runs[1].UserFollowup);

        // Spans: run 1 covers lines 1..3, run 2 covers lines 5..7.
        Assert.Equal(1, t.Runs[0].LineStart);
        Assert.Equal(3, t.Runs[0].LineEnd);
        Assert.Equal(5, t.Runs[1].LineStart);
        Assert.Equal(7, t.Runs[1].LineEnd);
    }

    [Fact]
    public void FailedRun_PreservesExitCodeAndStatus()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-dead" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            new() { Timestamp = T0.AddSeconds(1), Stream = "stderr", Text = "No conversation found with session ID: uuid-dead" },
            Sys(2, "[taskboard] claude CLI exited: status=failed, exitCode=1, duration=1.8s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(10));

        var r = Assert.Single(t.Runs);
        Assert.Equal("failed", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("uuid-dead", r.InputSessionId);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void ExitMarkerAfterNextEvent_IsNotMisattributed()
    {
        // Defensive: even though the product is sequential per project,
        // a torn write order could place an exit marker after the next
        // run's start. The builder must not pull a future exit into the
        // earlier run.
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude" },
            new() { Ts = T0.AddSeconds(50), Kind = "continue", Cli = "claude", Resumed = true }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            // First exit marker is missing entirely (torn line).
            Sys(50, "[taskboard] Started claude CLI (PID 1235)"),
            Sys(120, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=70s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(150));

        Assert.Equal(2, t.RunCount);
        // Run 1 has no exit marker before run 2's start - status falls
        // back to "running" (or "unknown" if no started marker either).
        Assert.NotEqual("completed", t.Runs[0].Status);
        Assert.Equal("completed", t.Runs[1].Status);
        Assert.Equal(70.0, t.Runs[1].DurationSeconds);
    }
}
