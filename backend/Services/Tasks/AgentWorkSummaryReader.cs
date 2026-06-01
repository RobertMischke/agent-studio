using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Folds the two per-job execution logs - <c>logs/session-events.jsonl</c>
/// and <c>logs/tool-calls.jsonl</c> - into the small <see cref="AgentWorkSummary"/>
/// the Overview tab renders. Both files are JSONL append-only, so the reader
/// is single-pass and tolerant: a torn or malformed line is skipped, never
/// throws. Missing files yield an empty summary (zero calls / zero tools).
///
/// <para>
/// The session-events file uses PascalCase keys (it is written through
/// <see cref="TaskSessionLog"/> with the default
/// <see cref="JsonSerializerOptions"/>); the tool-calls file uses
/// camelCase keys (written from anonymous objects). The reader handles
/// both by setting <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
/// on every parse.
/// </para>
/// </summary>
internal static class AgentWorkSummaryReader
{
    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AgentWorkSummary Read(TaskInfo info)
    {
        var sessionPath = TaskPaths.SessionEventsLog(info.FolderPath);
        var toolPath = ToolCallsLogPath(info.FolderPath);

        var (callCount, recovered, sessionStartedAt, sessionLastAt) = FoldSessionEvents(sessionPath);
        var (toolCalls, toolCounts, toolLastAt) = FoldToolCalls(toolPath);

        DateTime? lastTouch = sessionLastAt;
        if (toolLastAt is { } t && (lastTouch is null || t > lastTouch.Value))
            lastTouch = t;

        return new AgentWorkSummary
        {
            Calls = callCount,
            Recovered = recovered,
            ToolCalls = toolCalls,
            ToolCounts = toolCounts,
            StartedAt = sessionStartedAt,
            LastTouchAt = lastTouch,
            CurrentSessionId = info.SessionName,
        };
    }

    private static string ToolCallsLogPath(string jobFolder)
        => Path.Combine(TaskPaths.LogsDir(jobFolder), "tool-calls.jsonl");

    private static (int calls, bool recovered, DateTime? startedAt, DateTime? lastAt) FoldSessionEvents(string path)
    {
        if (!File.Exists(path)) return (0, false, null, null);
        int calls = 0;
        bool recovered = false;
        DateTime? earliest = null;
        DateTime? latest = null;
        foreach (var raw in ReadAllLinesSafe(path))
        {
            SessionEvent? evt;
            try { evt = JsonSerializer.Deserialize<SessionEvent>(raw, ParseOpts); }
            catch { continue; }
            if (evt == null) continue;
            calls++;
            if (string.Equals(evt.Kind, "recovery", StringComparison.OrdinalIgnoreCase)) recovered = true;
            var ts = evt.Ts;
            if (ts != default)
            {
                if (earliest is null || ts < earliest.Value) earliest = ts;
                if (latest is null || ts > latest.Value) latest = ts;
            }
        }
        return (calls, recovered, earliest, latest);
    }

    private static (int total, List<AgentWorkToolCount> counts, DateTime? lastAt) FoldToolCalls(string path)
    {
        if (!File.Exists(path)) return (0, new List<AgentWorkToolCount>(), null);
        int total = 0;
        DateTime? lastAt = null;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ReadAllLinesSafe(path))
        {
            ToolCallRow? row;
            try { row = JsonSerializer.Deserialize<ToolCallRow>(raw, ParseOpts); }
            catch { continue; }
            if (row == null) continue;
            // Track last activity from either kind so a job that crashed
            // mid-tool still surfaces the actual last signal.
            if (row.Ts is { } t && (lastAt is null || t > lastAt.Value)) lastAt = t;
            // Only count `started` rows toward the tool mix so a started+
            // completed pair is one tool call, not two.
            if (!string.Equals(row.Kind, "started", StringComparison.OrdinalIgnoreCase)) continue;
            total++;
            var name = string.IsNullOrWhiteSpace(row.Tool) ? "(unknown)" : row.Tool!.Trim();
            counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        var sorted = counts
            .Select(kv => new AgentWorkToolCount { Tool = kv.Key, Count = kv.Value })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Tool, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (total, sorted, lastAt);
    }

    private static IEnumerable<string> ReadAllLinesSafe(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { yield break; }
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // Strip UTF-8 BOM that can prefix the first line on Windows.
            var trimmed = raw.TrimStart('﻿');
            yield return trimmed;
        }
    }

    private sealed record ToolCallRow
    {
        public DateTime? Ts { get; init; }
        public string? Kind { get; init; }
        public string? Tool { get; init; }
    }
}
