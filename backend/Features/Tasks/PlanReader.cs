using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Builds the per-job <see cref="TaskPlanView"/> the plan strip renders by
/// replaying two append-only telemetry files the runtime already writes:
/// <c>logs/plan-snapshots.jsonl</c> (one line per Claude <c>TodoWrite</c> /
/// Codex <c>update_plan</c> frame) and <c>logs/tool-calls.jsonl</c> (one line
/// per tool started / completed). No model call, no second LLM: the data is on
/// disk and this reader folds it. Missing or torn lines are skipped, never throw.
///
/// <para>
/// <b>Sub-action attribution</b> is a pure replay (taxonomy.md "Derivation
/// rules"): walk snapshots and tool-starts in merged timestamp order, track the
/// single <c>active</c> item id, and attribute each tool call to whichever item
/// was active when it fired. Tool calls before the first plan land in the
/// "before plan" bucket. The two plan-frame tools themselves
/// (<c>TodoWrite</c> / <c>update_plan</c>) are never counted as sub-actions.
/// </para>
/// <para>
/// <b>Soft-estimate median</b> is the median sub-action count across items that
/// have already reached <c>done</c>; it is suppressed below two samples so we
/// never draw a reference band from a single data point.
/// </para>
/// </summary>
internal static class PlanReader
{
    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> PlanFrameTools =
        new(StringComparer.OrdinalIgnoreCase) { "TodoWrite", "update_plan" };

    public static TaskPlanView Read(TaskInfo info)
    {
        var snapshotPath = Path.Combine(TaskPaths.LogsDir(info.FolderPath), "plan-snapshots.jsonl");
        var toolPath = Path.Combine(TaskPaths.LogsDir(info.FolderPath), "tool-calls.jsonl");

        var snapshots = ReadSnapshots(snapshotPath);
        if (snapshots.Count == 0)
            return new TaskPlanView { HasPlan = false };

        var latest = snapshots[^1];
        var toolStarts = ReadToolStarts(toolPath);

        // Merge snapshots + tool-starts in timestamp order and attribute each
        // tool call to the item that was active when it fired.
        var subActionsByItem = new Dictionary<string, List<TaskPlanSubAction>>(StringComparer.Ordinal);
        var unassigned = new List<TaskPlanSubAction>();
        string? currentItemId = null;

        int si = 0, ti = 0;
        while (si < snapshots.Count || ti < toolStarts.Count)
        {
            var takeSnapshot = ti >= toolStarts.Count
                || (si < snapshots.Count && snapshots[si].Ts <= toolStarts[ti].Ts);
            if (takeSnapshot)
            {
                var active = snapshots[si].Items.FirstOrDefault(i => i.Status == "active");
                if (active != null) currentItemId = active.Id;
                si++;
            }
            else
            {
                var ts = toolStarts[ti];
                var sub = new TaskPlanSubAction { Ts = ts.Ts, Tool = ts.Tool, Label = ts.Label };
                if (currentItemId == null) unassigned.Add(sub);
                else
                {
                    if (!subActionsByItem.TryGetValue(currentItemId, out var list))
                        subActionsByItem[currentItemId] = list = new List<TaskPlanSubAction>();
                    list.Add(sub);
                }
                ti++;
            }
        }

        // Project the latest snapshot's items with their derived sub-actions.
        var items = new List<TaskPlanItemView>(latest.Items.Count);
        foreach (var it in latest.Items)
        {
            subActionsByItem.TryGetValue(it.Id, out var subs);
            subs ??= new List<TaskPlanSubAction>();
            items.Add(new TaskPlanItemView
            {
                Id = it.Id,
                Title = it.Title,
                Status = it.Status,
                SubActionCount = subs.Count,
                SubActions = subs,
            });
        }

        var activeItemId = items.FirstOrDefault(i => i.Status == "active")?.Id;
        var doneCounts = items.Where(i => i.Status == "done").Select(i => i.SubActionCount).ToList();
        int? median = doneCounts.Count >= 2 ? Median(doneCounts) : null;

        return new TaskPlanView
        {
            HasPlan = true,
            Source = latest.Source,
            SnapshotCount = snapshots.Count,
            ActiveItemId = activeItemId,
            SoftEstimateMedian = median,
            Items = items,
            UnassignedSubActions = unassigned,
        };
    }

    private static int Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        if (n % 2 == 1) return sorted[n / 2];
        return (int)Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0, MidpointRounding.AwayFromZero);
    }

    private static List<SnapshotRow> ReadSnapshots(string path)
    {
        var result = new List<SnapshotRow>();
        foreach (var raw in ReadAllLinesSafe(path))
        {
            SnapshotJson? row;
            try { row = JsonSerializer.Deserialize<SnapshotJson>(raw, ParseOpts); }
            catch { continue; }
            if (row?.Items == null || row.Items.Count == 0) continue;
            var items = row.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i => new PlanFrameItem(i.Id!, i.Title ?? "", PlanItemStatus.Normalize(i.Status)))
                .ToList();
            if (items.Count == 0) continue;
            result.Add(new SnapshotRow(row.Ts ?? DateTime.MinValue, row.Source ?? "", items));
        }
        // Snapshots are written in order, but sort defensively so a torn /
        // out-of-order tail cannot scramble the replay.
        result.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        return result;
    }

    private static List<ToolStartRow> ReadToolStarts(string path)
    {
        var result = new List<ToolStartRow>();
        ToolStartRow? prev = null;
        foreach (var raw in ReadAllLinesSafe(path))
        {
            ToolCallJson? row;
            try { row = JsonSerializer.Deserialize<ToolCallJson>(raw, ParseOpts); }
            catch { continue; }
            if (row == null) continue;
            if (!string.Equals(row.Kind, "started", StringComparison.OrdinalIgnoreCase)) continue;
            var tool = (row.Tool ?? "").Trim();
            if (tool.Length == 0 || PlanFrameTools.Contains(tool)) continue;
            var ts = row.Ts ?? DateTime.MinValue;
            var label = BuildLabel(tool, row.Argument);

            // Drop a duplicate that streaming partial-frames can produce: the
            // same tool + argument repeated within a second is one logical call.
            if (prev != null && prev.Tool == tool && prev.Label == label
                && (ts - prev.Ts).Duration() < TimeSpan.FromSeconds(1))
                continue;

            var cur = new ToolStartRow(ts, tool, label);
            result.Add(cur);
            prev = cur;
        }
        result.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        return result;
    }

    private static string BuildLabel(string tool, string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return tool;
        var arg = argument.Trim();
        // For path-ish arguments show the leaf so the label stays one line.
        if (arg.IndexOf('/') >= 0 || arg.IndexOf('\\') >= 0)
        {
            var leaf = arg.Replace('\\', '/').TrimEnd('/');
            var slash = leaf.LastIndexOf('/');
            if (slash >= 0 && slash < leaf.Length - 1) arg = leaf[(slash + 1)..];
        }
        if (arg.Length > 60) arg = arg[..60] + "…";
        return $"{tool} {arg}";
    }

    private static IEnumerable<string> ReadAllLinesSafe(string path)
    {
        if (!File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { yield break; }
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            yield return raw.TrimStart('﻿');
        }
    }

    private sealed record SnapshotRow(DateTime Ts, string Source, List<PlanFrameItem> Items);
    private sealed record ToolStartRow(DateTime Ts, string Tool, string Label);

    private sealed record SnapshotJson
    {
        public DateTime? Ts { get; init; }
        public string? Source { get; init; }
        public List<SnapshotItemJson>? Items { get; init; }
    }

    private sealed record SnapshotItemJson
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Status { get; init; }
    }

    private sealed record ToolCallJson
    {
        public DateTime? Ts { get; init; }
        public string? Kind { get; init; }
        public string? Tool { get; init; }
        public string? Argument { get; init; }
    }
}
