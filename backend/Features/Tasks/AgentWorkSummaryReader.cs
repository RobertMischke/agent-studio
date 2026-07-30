using System.Text.Json;

namespace AgentStudio.Tasks;

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

    public static AgentWorkSummary Read(TaskInfo info, TaskTokenSummary? tokenSummary = null)
    {
        var sessionPath = TaskPaths.SessionEventsLog(info.FolderPath);
        var toolPath = ToolCallsLogPath(info.FolderPath);

        var (sessionCalls, recovered, sessionStartedAt, sessionLastAt, remote) =
            FoldSessionEvents(sessionPath);
        var (toolCalls, toolCounts, toolLastAt) = FoldToolCalls(toolPath);
        var callCount = remote && tokenSummary is not null
            ? Math.Max(sessionCalls, tokenSummary.Calls)
            : sessionCalls;
        var ledgerStartedAt = remote
            ? tokenSummary?.Entries.Where(call => call.Ts != default).Select(call => (DateTime?)call.Ts).Min()
            : null;
        if (ledgerStartedAt.HasValue
            && (!sessionStartedAt.HasValue || ledgerStartedAt.Value < sessionStartedAt.Value))
        {
            sessionStartedAt = ledgerStartedAt;
        }

        DateTime? lastTouch = sessionLastAt;
        if (toolLastAt is { } t && (lastTouch is null || t > lastTouch.Value))
            lastTouch = t;
        if (remote && tokenSummary?.LastUpdate is { } tokenLast
            && (lastTouch is null || tokenLast > lastTouch.Value))
        {
            lastTouch = tokenLast;
        }

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

    /// <summary>
    /// Drill-down companion to <see cref="Read"/>: folds the same
    /// <c>logs/tool-calls.jsonl</c> into per-tool groups, each carrying the
    /// individual calls (started argument paired with the completed outcome)
    /// so the Overview tab can show *what* the agent did, not just a count.
    /// Single-pass and tolerant like <see cref="Read"/>; a torn line is
    /// skipped, a missing file yields an empty detail. The per-group call
    /// list is capped at <paramref name="maxCallsPerGroup"/> (most recent
    /// kept) so a pathological job cannot produce an unbounded payload; the
    /// group's <c>Count</c> stays the honest uncapped total.
    /// </summary>
    public static AgentWorkDetail ReadDetail(TaskInfo info, int maxCallsPerGroup = 250)
    {
        var toolPath = ToolCallsLogPath(info.FolderPath);
        if (!File.Exists(toolPath)) return new AgentWorkDetail();

        // Preserve first-seen order of tools; within a tool, calls in
        // chronological (file) order. Open calls (started without a matching
        // completed yet) are tracked per tool so the next completed row of the
        // same tool pairs with the most recent open one.
        var order = new List<string>();
        var byTool = new Dictionary<string, List<MutableCall>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        foreach (var raw in ReadAllLinesSafe(toolPath))
        {
            ToolCallRow? row;
            try { row = JsonSerializer.Deserialize<ToolCallRow>(raw, ParseOpts); }
            catch { continue; }
            if (row == null) continue;
            var name = string.IsNullOrWhiteSpace(row.Tool) ? "(unknown)" : row.Tool!.Trim();

            if (string.Equals(row.Kind, "started", StringComparison.OrdinalIgnoreCase))
            {
                if (!byTool.TryGetValue(name, out var list))
                {
                    list = new List<MutableCall>();
                    byTool[name] = list;
                    order.Add(name);
                }
                list.Add(new MutableCall { Ts = row.Ts, Argument = row.Argument });
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                total++;
            }
            else if (string.Equals(row.Kind, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (byTool.TryGetValue(name, out var list))
                {
                    // Attach to the most recent still-open call of this tool.
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].Completed) continue;
                        list[i].Completed = true;
                        list[i].IsError = row.IsError;
                        list[i].ResultFirstLine = row.FirstLine;
                        break;
                    }
                }
            }
        }

        var groups = order
            .Select(tool => new AgentWorkToolGroup
            {
                Tool = tool,
                Count = counts.TryGetValue(tool, out var c) ? c : byTool[tool].Count,
                Calls = CapMostRecent(byTool[tool], maxCallsPerGroup)
                    .Select(m => m.ToRecord())
                    .ToList(),
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Tool, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentWorkDetail { Groups = groups, TotalCalls = total };
    }

    /// <summary>Keep the most recent <paramref name="max"/> calls, preserving chronological order.</summary>
    private static IEnumerable<MutableCall> CapMostRecent(List<MutableCall> calls, int max)
        => calls.Count <= max ? calls : calls.Skip(calls.Count - max);

    private static string ToolCallsLogPath(string jobFolder)
        => Path.Combine(TaskPaths.LogsDir(jobFolder), "tool-calls.jsonl");

    private static (int calls, bool recovered, DateTime? startedAt, DateTime? lastAt, bool remote) FoldSessionEvents(string path)
    {
        if (!File.Exists(path)) return (0, false, null, null, false);
        int calls = 0;
        bool recovered = false;
        bool remote = false;
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
            remote = string.Equals(evt.Cli, "remote-runner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(evt.ExecutionLocation?.ExecutionKind, "remote", StringComparison.OrdinalIgnoreCase);
            var ts = evt.Ts;
            if (ts != default)
            {
                if (earliest is null || ts < earliest.Value) earliest = ts;
                if (latest is null || ts > latest.Value) latest = ts;
            }
        }
        return (calls, recovered, earliest, latest, remote);
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
        // Detail-only fields. The summary path ignores them; ReadDetail pairs
        // the started argument with the completed outcome.
        public string? Argument { get; init; }
        public bool? IsError { get; init; }
        public string? FirstLine { get; init; }
    }

    /// <summary>Mutable accumulator for one call while pairing started/completed rows.</summary>
    private sealed class MutableCall
    {
        public DateTime? Ts { get; init; }
        public string? Argument { get; init; }
        public bool Completed { get; set; }
        public bool? IsError { get; set; }
        public string? ResultFirstLine { get; set; }

        public AgentWorkCall ToRecord() => new()
        {
            Ts = Ts,
            Argument = Argument,
            Completed = Completed,
            IsError = IsError,
            ResultFirstLine = ResultFirstLine,
        };
    }
}
