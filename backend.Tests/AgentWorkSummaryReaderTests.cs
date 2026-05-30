using System.Text;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the per-job rollup that drives the Overview tab's Agent Work
/// block (the row that replaced the inert SESSION row). The reader folds
/// <c>logs/session-events.jsonl</c> + <c>logs/tool-calls.jsonl</c> into a
/// small typed view; the underlying log shape comes from two writers
/// (<see cref="TaskSessionLog"/> and <c>ProjectRunner.AppendToolCallLog</c>)
/// that use *different* JSON key casings. A regression in either file's
/// shape silently empties the Overview block without these tests.
/// </summary>
public class AgentWorkSummaryReaderTests
{
    private static TaskInfo MakeJob(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, TaskPaths.LogsDirName));
        return new TaskInfo
        {
            Id = "test-job",
            FolderPath = folder,
            SessionName = "sess-fixture-1",
        };
    }

    [Fact]
    public void Read_EmptyFolder_ReturnsZeroSummary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agent-work-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = MakeJob(folder);
            var summary = AgentWorkSummaryReader.Read(info);
            Assert.Equal(0, summary.Calls);
            Assert.Equal(0, summary.ToolCalls);
            Assert.Empty(summary.ToolCounts);
            Assert.Null(summary.StartedAt);
            Assert.Null(summary.LastTouchAt);
            Assert.False(summary.Recovered);
            Assert.Equal("sess-fixture-1", summary.CurrentSessionId);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Read_FoldsSessionEventsAndToolCalls()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agent-work-fold-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = MakeJob(folder);
            var logsDir = TaskPaths.LogsDir(folder);

            // Session events (PascalCase, matches the TaskSessionLog writer).
            var sessionLines = new[]
            {
                "{\"Ts\":\"2026-05-28T19:00:00Z\",\"Kind\":\"start\",\"Cli\":\"claude\",\"InputSessionId\":null,\"CapturedSessionId\":\"sess-1\",\"Resumed\":false,\"Reason\":null,\"HeadShaBefore\":null,\"HeadShaAfter\":null}",
                "{\"Ts\":\"2026-05-28T19:30:00Z\",\"Kind\":\"continue\",\"Cli\":\"claude\",\"InputSessionId\":\"sess-1\",\"CapturedSessionId\":\"sess-1\",\"Resumed\":true,\"Reason\":null,\"HeadShaBefore\":null,\"HeadShaAfter\":null}",
                "{\"Ts\":\"2026-05-28T20:00:00Z\",\"Kind\":\"recovery\",\"Cli\":\"claude\",\"InputSessionId\":null,\"CapturedSessionId\":null,\"Resumed\":false,\"Reason\":\"session lost\",\"HeadShaBefore\":null,\"HeadShaAfter\":null}",
            };
            File.WriteAllLines(Path.Combine(logsDir, TaskPaths.SessionEventsLogFileName), sessionLines, Encoding.UTF8);

            // Tool calls (camelCase, matches ProjectRunner.AppendToolCallLog).
            // Two Read starts + their completed pairs, one Edit start + completed,
            // one stray started-without-completion. Counting only `started` rows
            // keeps the tally honest (4 starts) even when completions arrive.
            var toolLines = new[]
            {
                "{\"ts\":\"2026-05-28T19:05:00Z\",\"kind\":\"started\",\"tool\":\"Read\",\"argument\":\"/a\"}",
                "{\"ts\":\"2026-05-28T19:05:01Z\",\"kind\":\"completed\",\"tool\":\"Read\",\"isError\":false,\"firstLine\":\"\"}",
                "{\"ts\":\"2026-05-28T19:10:00Z\",\"kind\":\"started\",\"tool\":\"Read\",\"argument\":\"/b\"}",
                "{\"ts\":\"2026-05-28T19:10:02Z\",\"kind\":\"completed\",\"tool\":\"Read\",\"isError\":false,\"firstLine\":\"\"}",
                "{\"ts\":\"2026-05-28T19:15:00Z\",\"kind\":\"started\",\"tool\":\"Edit\",\"argument\":\"/c\"}",
                "{\"ts\":\"2026-05-28T19:15:03Z\",\"kind\":\"completed\",\"tool\":\"Edit\",\"isError\":false,\"firstLine\":\"\"}",
                "{\"ts\":\"2026-05-28T20:05:00Z\",\"kind\":\"started\",\"tool\":\"Bash\",\"argument\":\"echo hi\"}",
            };
            File.WriteAllLines(Path.Combine(logsDir, "tool-calls.jsonl"), toolLines, Encoding.UTF8);

            var summary = AgentWorkSummaryReader.Read(info);

            Assert.Equal(3, summary.Calls);
            Assert.True(summary.Recovered);
            Assert.Equal(4, summary.ToolCalls);
            Assert.Collection(summary.ToolCounts,
                first => { Assert.Equal("Read", first.Tool); Assert.Equal(2, first.Count); },
                second => { Assert.Equal("Bash", second.Tool); Assert.Equal(1, second.Count); },
                third => { Assert.Equal("Edit", third.Tool); Assert.Equal(1, third.Count); });
            Assert.Equal(new DateTime(2026, 5, 28, 19, 0, 0, DateTimeKind.Utc), summary.StartedAt);
            // Last-touch is the max across the two streams; the Bash start
            // at 20:05 wins over the last session event at 20:00.
            Assert.Equal(new DateTime(2026, 5, 28, 20, 5, 0, DateTimeKind.Utc), summary.LastTouchAt);
            Assert.Equal("sess-fixture-1", summary.CurrentSessionId);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Read_TolerantToMalformedLines_AndBom()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agent-work-tolerant-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = MakeJob(folder);
            var logsDir = TaskPaths.LogsDir(folder);
            // BOM-prefixed first line, then a torn line, then a valid line.
            var content =
                "﻿{\"Ts\":\"2026-05-28T19:00:00Z\",\"Kind\":\"start\",\"Cli\":\"claude\"}\n" +
                "this is not json\n" +
                "{\"Ts\":\"2026-05-28T19:10:00Z\",\"Kind\":\"continue\",\"Cli\":\"claude\"}\n";
            File.WriteAllText(Path.Combine(logsDir, TaskPaths.SessionEventsLogFileName), content, Encoding.UTF8);

            // Tool log with a torn line in the middle.
            var toolContent =
                "{\"ts\":\"2026-05-28T19:05:00Z\",\"kind\":\"started\",\"tool\":\"Read\"}\n" +
                "{ broken row \n" +
                "{\"ts\":\"2026-05-28T19:06:00Z\",\"kind\":\"started\",\"tool\":\"Bash\"}\n";
            File.WriteAllText(Path.Combine(logsDir, "tool-calls.jsonl"), toolContent, Encoding.UTF8);

            var summary = AgentWorkSummaryReader.Read(info);
            Assert.Equal(2, summary.Calls);
            Assert.Equal(2, summary.ToolCalls);
            Assert.False(summary.Recovered);
        }
        finally { Directory.Delete(folder, true); }
    }
}
