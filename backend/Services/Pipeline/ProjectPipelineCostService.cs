using System.Collections.Concurrent;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Project-level rollup of pipeline step cost over time: "how it develops"
/// per the original ask. Walks every task folder in a project, reads each
/// <c>pipeline-execution.json</c> through <see cref="PipelineExecutionLog"/>,
/// and folds the per-step token usage into (step-kind, day) cells priced
/// through the single <see cref="TokenPricing"/> table. The frontend
/// renders the result as a stacked time trend in the project Token Usage
/// surface, next to the orchestrator-log heatmap.
///
/// <para>
/// This is the deliberately-separate, cached path that
/// <see cref="PipelineCostCalculator"/> refers to: the per-task Overview
/// poll never triggers this O(tasks) disk walk; only the analytics panel
/// does, and a short TTL cache absorbs repeat opens within a window.
/// </para>
/// </summary>
public sealed class ProjectPipelineCostService
{
    public const int DefaultDays = 30;
    public const int MaxDays = 180;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TaskScannerService _scanner;
    private readonly PipelineExecutionLog _log;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectPipelineCostTimeline Value)> _cache = new();

    public ProjectPipelineCostService(TaskScannerService scanner, PipelineExecutionLog log)
    {
        _scanner = scanner;
        _log = log;
    }

    /// <summary>
    /// Build the per-step-kind cost timeline for one project. Reads each
    /// task folder's pipeline-execution.json once; result is cached for a
    /// short TTL so a panel that re-fetches on tab switches does not
    /// re-walk the disk every time.
    /// </summary>
    public ProjectPipelineCostTimeline Build(string projectName, string watchPath, int days, DateTime? nowUtc = null)
    {
        var d = ResolveDays(days);
        var cacheKey = $"{watchPath}|{d}";
        if (nowUtc == null
            && _cache.TryGetValue(cacheKey, out var hit)
            && DateTime.UtcNow - hit.At < CacheTtl)
        {
            return hit.Value;
        }

        var records = new List<PipelineExecutionRecord>();
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            foreach (var task in _scanner.ScanAllJobs())
            {
                if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(task.FolderPath)) continue;
                var rec = _log.Read(task.FolderPath);
                if (rec != null) records.Add(rec);
            }
        }

        var timeline = BuildFromRecords(projectName, records, d, nowUtc);
        if (nowUtc == null)
        {
            _cache[cacheKey] = (DateTime.UtcNow, timeline);
        }
        return timeline;
    }

    /// <summary>
    /// Pure overload: aggregate pre-loaded execution records. Used by unit
    /// tests to avoid filesystem round-trips.
    /// </summary>
    public static ProjectPipelineCostTimeline BuildFromRecords(
        string projectName,
        IReadOnlyList<PipelineExecutionRecord> records,
        int days,
        DateTime? nowUtc = null)
    {
        var d = ResolveDays(days);
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var endDay = AlignDay(now);
        var startDay = endDay.AddDays(-(d - 1));

        var dayList = new List<string>(d);
        for (var i = 0; i < d; i++)
            dayList.Add(endDay.AddDays(-(d - 1 - i)).ToString("yyyy-MM-dd"));

        var perKindDay = new Dictionary<(StepKind Kind, string Day), Acc>();
        var perKind = new Dictionary<StepKind, Acc>();
        long grandTokens = 0;
        decimal grandCost = 0m;
        var grandUnknown = false;
        var contributingTasks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rec in records)
        {
            // Bucket the whole run by its completion (or, while still
            // running, its start) so a task's spend lands on the day it ran.
            var ts = (rec.CompletedAt ?? rec.StartedAt).ToUniversalTime();
            if (ts < startDay || ts >= endDay.AddDays(1)) continue;
            var dayKey = AlignDay(ts).ToString("yyyy-MM-dd");
            var contributed = false;

            foreach (var s in rec.Steps)
            {
                var stepTokens = s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens;
                if (stepTokens <= 0) continue;
                var est = TokenPricing.Estimate(
                    s.Model, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheCreationTokens);

                Add(perKindDay, (s.Kind, dayKey), stepTokens, est);
                Add(perKind, s.Kind, stepTokens, est);
                grandTokens += stepTokens;
                if (est.ModelKnown) grandCost += est.Total; else grandUnknown = true;
                contributed = true;
            }
            if (contributed) contributingTasks.Add(rec.JobId);
        }

        var series = new List<PipelineKindSeries>();
        foreach (var kind in KindOrder)
        {
            if (!perKind.TryGetValue(kind, out var kindAcc)) continue;
            var cells = new List<PipelineKindDayCell>(d);
            foreach (var day in dayList)
            {
                perKindDay.TryGetValue((kind, day), out var cell);
                cells.Add(new PipelineKindDayCell(
                    Day: day,
                    TotalTokens: cell?.Tokens ?? 0,
                    CostUsd: Round(cell?.Cost ?? 0m)));
            }
            series.Add(new PipelineKindSeries(
                Kind: KindKey(kind),
                TotalTokens: kindAcc.Tokens,
                TotalCostUsd: Round(kindAcc.Cost),
                AnyModelUnknown: kindAcc.AnyUnknown,
                Cells: cells));
        }

        return new ProjectPipelineCostTimeline(
            Project: projectName,
            Days: dayList,
            WindowDays: d,
            Kinds: series,
            TotalTokens: grandTokens,
            TotalCostUsd: Round(grandCost),
            AnyModelUnknown: grandUnknown,
            TaskCount: contributingTasks.Count,
            HasData: grandTokens > 0,
            FetchedAt: DateTime.UtcNow.ToString("o"));
    }

    public static int ResolveDays(int requested) =>
        requested <= 0 ? DefaultDays : Math.Min(requested, MaxDays);

    // Stable render order: core run first, then the model-driven aspects,
    // deterministic tool steps, the orchestrator decision, and finally any
    // generic module step.
    private static readonly StepKind[] KindOrder =
        [StepKind.Core, StepKind.Aspect, StepKind.Tool, StepKind.Orchestrator, StepKind.Drift, StepKind.Module];

    private static string KindKey(StepKind kind) => kind switch
    {
        StepKind.Core => "core",
        StepKind.Aspect => "aspect",
        StepKind.Tool => "tool",
        StepKind.Orchestrator => "orchestrator",
        StepKind.Drift => "drift",
        _ => "module",
    };

    private static void Add<TKey>(Dictionary<TKey, Acc> map, TKey key, long tokens, TokenCostEstimate est)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new Acc();
            map[key] = acc;
        }
        acc.Tokens += tokens;
        if (est.ModelKnown) acc.Cost += est.Total; else acc.AnyUnknown = true;
    }

    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static DateTime AlignDay(DateTime ts)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Acc
    {
        public long Tokens;
        public decimal Cost;
        public bool AnyUnknown;
    }
}

/// <summary>
/// Per-step-kind cost timeline for one project. <see cref="Days"/> are
/// UTC midnights, oldest to newest, the full requested window (so the
/// chart x-axis is dense even on days with no activity). Each
/// <see cref="PipelineKindSeries"/> carries one cell per day aligned to
/// <see cref="Days"/>.
/// </summary>
public sealed record ProjectPipelineCostTimeline(
    string Project,
    IReadOnlyList<string> Days,
    int WindowDays,
    IReadOnlyList<PipelineKindSeries> Kinds,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int TaskCount,
    bool HasData,
    string FetchedAt);

/// <summary>
/// One step-kind's series over the window. <see cref="Kind"/> is the
/// lowercase wire token (<c>core</c> / <c>aspect</c> / <c>tool</c> /
/// <c>orchestrator</c> / <c>module</c>) matching the frontend StepKind
/// type. <see cref="AnyModelUnknown"/> is true when at least one
/// contributing step used a model with no price on file, so the cost is
/// a lower bound.
/// </summary>
public sealed record PipelineKindSeries(
    string Kind,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    IReadOnlyList<PipelineKindDayCell> Cells);

public sealed record PipelineKindDayCell(
    string Day,
    long TotalTokens,
    decimal CostUsd);
