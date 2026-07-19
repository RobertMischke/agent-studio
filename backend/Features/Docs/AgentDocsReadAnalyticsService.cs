using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Docs;

/// <summary>
/// Real Tool-Use Read Analytics for the project Agent Docs surface: counts how
/// often each CLI tool-use read consumed each agent instruction file. Replaces
/// the placeholder mock the panel previously rendered.
///
/// <para>
/// The inventory of what counts as an agent doc comes from
/// <see cref="ProjectSteeringDocsService"/> (AGENTS.md, scoped AGENTS.md,
/// CLAUDE.md, GEMINI.md, and Copilot instructions that actually exist), so this
/// service never reports a file outside the inventory. The read evidence comes
/// from each task folder's append-only <c>logs/tool-calls.jsonl</c>: every
/// <c>started</c> row is classified by <see cref="AgentDocReadClassifier"/> and,
/// when its target path resolves to an inventory file, counted against that file
/// and CLI.
/// </para>
///
/// <para>
/// Visibility, not enforcement: like the token-usage panels this only reads
/// logs. It walks the project's task folders once per call behind a short TTL
/// cache (the same pattern as <c>ProjectPipelineCostService</c>) so repeat panel
/// opens do not re-walk disk.
/// </para>
/// </summary>
public sealed class AgentDocsReadAnalyticsService
{
    public const int DefaultWindowDays = 7;
    public const int MaxWindowDays = 90;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly ProjectSteeringDocsService _docs;
    private readonly ILogger<AgentDocsReadAnalyticsService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, AgentDocsReadAnalytics Value)> _cache = new();

    public AgentDocsReadAnalyticsService(
        TaskScannerService scanner,
        ProjectSteeringDocsService docs,
        ILogger<AgentDocsReadAnalyticsService> logger)
    {
        _scanner = scanner;
        _docs = docs;
        _logger = logger;
    }

    /// <summary>
    /// Build read analytics for one project, or <c>null</c> when the project is
    /// unknown (mirrors <see cref="ProjectSteeringDocsService.GetOverview"/>).
    /// A known project with no agent docs, or with docs but no read evidence,
    /// returns a populated result with <see cref="AgentDocsReadAnalytics.HasData"/>
    /// = false so the UI can render an honest empty state instead of the old
    /// fabricated numbers.
    /// </summary>
    public AgentDocsReadAnalytics? GetAnalytics(string projectName, int windowDays = DefaultWindowDays, DateTime? nowUtc = null)
    {
        var overview = _docs.GetOverview(projectName);
        if (overview == null) return null;

        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
        var watchPath = entry?.Path ?? string.Empty;

        var window = ResolveWindow(windowDays);
        var cacheKey = $"{projectName}|{watchPath}|{window}";
        if (nowUtc == null
            && _cache.TryGetValue(cacheKey, out var hit)
            && DateTime.UtcNow - hit.At < CacheTtl)
        {
            return hit.Value;
        }

        var sw = Stopwatch.StartNew();
        var inventory = overview.Sources
            .Select(s => s.RelPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var accumulators = overview.Sources.ToDictionary(
            s => s.RelPath,
            s => new FileAccumulator(s.RelPath, s.Label),
            StringComparer.OrdinalIgnoreCase);

        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var recentCutoff = now.AddDays(-window);

        var tasksScanned = 0;
        var contributingTasks = new HashSet<string>(StringComparer.Ordinal);

        if (inventory.Count > 0 && !string.IsNullOrWhiteSpace(watchPath))
        {
            foreach (var task in _scanner.ScanAllJobs())
            {
                if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(task.FolderPath)) continue;
                tasksScanned++;
                var taskId = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!;
                if (FoldTaskReads(task.FolderPath, taskId, inventory, accumulators, recentCutoff))
                    contributingTasks.Add(taskId);
            }
        }

        var files = accumulators.Values
            .Select(a => a.ToDto())
            .OrderByDescending(f => f.Reads)
            .ThenByDescending(f => f.LastReadAt ?? DateTime.MinValue)
            .ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byCli = accumulators.Values
            .SelectMany(a => a.CliCounts)
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AgentDocsReadCliTotal(g.Key, g.Sum(kv => kv.Value)))
            .OrderByDescending(c => c.Reads)
            .ThenBy(c => c.Cli, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalReads = files.Sum(f => f.Reads);
        var recentReads = files.Sum(f => f.RecentReads);
        var lastReadAt = files
            .Where(f => f.LastReadAt.HasValue)
            .Select(f => f.LastReadAt!.Value)
            .DefaultIfEmpty(default)
            .Max();

        var result = new AgentDocsReadAnalytics(
            ProjectName: projectName,
            BaseDir: overview.BaseDir,
            WindowDays: window,
            HasData: totalReads > 0,
            TotalReads: totalReads,
            RecentReads: recentReads,
            TaskCount: contributingTasks.Count,
            LastReadAt: lastReadAt == default ? null : lastReadAt,
            Files: files,
            ByCli: byCli,
            GeneratedAt: DateTime.UtcNow.ToString("o"));

        sw.Stop();
        _logger.LogInformation(
            "agent-docs-read-analytics project={Project} files={Files} reads={Reads} recentReads={RecentReads} tasksScanned={TasksScanned} contributingTasks={ContributingTasks} windowDays={WindowDays} elapsedMs={ElapsedMs}",
            projectName, files.Count, totalReads, recentReads, tasksScanned, contributingTasks.Count, window, sw.ElapsedMilliseconds);

        if (nowUtc == null)
        {
            _cache[cacheKey] = (DateTime.UtcNow, result);
        }
        return result;
    }

    /// <summary>
    /// Fold one task's <c>tool-calls.jsonl</c> into the per-file accumulators.
    /// Single-pass and tolerant: a torn or malformed line is skipped, a missing
    /// file contributes nothing. Returns true when the task produced at least one
    /// matched read so the caller can count distinct contributing tasks.
    /// </summary>
    private bool FoldTaskReads(
        string folderPath,
        string taskId,
        IReadOnlyCollection<string> inventory,
        Dictionary<string, FileAccumulator> accumulators,
        DateTime recentCutoff)
    {
        var path = Path.Combine(TaskPaths.LogsDir(folderPath), "tool-calls.jsonl");
        if (!File.Exists(path)) return false;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "agent-docs-read-analytics: could not read tool-calls for task {TaskId}", taskId);
            return false;
        }

        var contributed = false;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            ToolCallRow? row;
            try { row = JsonSerializer.Deserialize<ToolCallRow>(raw.TrimStart('﻿'), ParseOpts); }
            catch { continue; }
            if (row == null) continue;
            if (!string.Equals(row.Kind, "started", StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = AgentDocReadClassifier.Classify(row.Tool, row.Argument);
            if (candidate == null) continue;

            // A single command may name several files; count one read per
            // distinct inventory file it touched, but never double-count the
            // same file for one row.
            var matchedThisRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in candidate.Paths)
            {
                var rel = AgentDocReadClassifier.MatchInventory(p, inventory);
                if (rel == null || !matchedThisRow.Add(rel)) continue;
                if (!accumulators.TryGetValue(rel, out var acc)) continue;
                acc.Add(candidate.Cli, row.Ts, recentCutoff, taskId);
                contributed = true;
            }
        }
        return contributed;
    }

    public static int ResolveWindow(int requested) =>
        requested <= 0 ? DefaultWindowDays : Math.Min(requested, MaxWindowDays);

    private sealed record ToolCallRow
    {
        public DateTime? Ts { get; init; }
        public string? Kind { get; init; }
        public string? Tool { get; init; }
        public string? Argument { get; init; }
    }

    /// <summary>Mutable per-file tally folded across every task in the project.</summary>
    private sealed class FileAccumulator
    {
        private readonly string _relPath;
        private readonly string _label;
        public readonly Dictionary<string, int> CliCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tasks = new(StringComparer.Ordinal);
        private int _reads;
        private int _recentReads;
        private DateTime? _lastReadAt;

        public FileAccumulator(string relPath, string label)
        {
            _relPath = relPath;
            _label = label;
        }

        public void Add(string cli, DateTime? ts, DateTime recentCutoff, string taskId)
        {
            _reads++;
            CliCounts[cli] = CliCounts.TryGetValue(cli, out var c) ? c + 1 : 1;
            _tasks.Add(taskId);
            if (ts is { } t)
            {
                var utc = t.ToUniversalTime();
                if (_lastReadAt == null || utc > _lastReadAt.Value) _lastReadAt = utc;
                if (utc >= recentCutoff) _recentReads++;
            }
        }

        public AgentDocsReadFile ToDto() => new(
            RelPath: _relPath,
            Label: _label,
            Reads: _reads,
            RecentReads: _recentReads,
            TaskCount: _tasks.Count,
            LastReadAt: _lastReadAt,
            ByCli: CliCounts
                .Select(kv => new AgentDocsReadCliCount(kv.Key, kv.Value))
                .OrderByDescending(c => c.Reads)
                .ThenBy(c => c.Cli, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }
}

/// <summary>
/// Per-file read tally for one agent doc. <see cref="Reads"/> is the lifetime
/// total across every task run in the project; <see cref="RecentReads"/> is the
/// subset inside the requested recency window; <see cref="ByCli"/> splits the
/// lifetime total by the CLI that issued each read.
/// </summary>
public sealed record AgentDocsReadFile(
    string RelPath,
    string Label,
    int Reads,
    int RecentReads,
    int TaskCount,
    DateTime? LastReadAt,
    IReadOnlyList<AgentDocsReadCliCount> ByCli);

public sealed record AgentDocsReadCliCount(string Cli, int Reads);

public sealed record AgentDocsReadCliTotal(string Cli, int Reads);

/// <summary>
/// Project-level Tool-Use Read Analytics. <see cref="Files"/> lists every agent
/// doc in the inventory (most-read first), including files with zero reads so
/// the UI can show honest zeros; <see cref="HasData"/> is false when no read was
/// observed at all, which drives the "no indexed usage yet" placeholder.
/// </summary>
public sealed record AgentDocsReadAnalytics(
    string ProjectName,
    string BaseDir,
    int WindowDays,
    bool HasData,
    int TotalReads,
    int RecentReads,
    int TaskCount,
    DateTime? LastReadAt,
    IReadOnlyList<AgentDocsReadFile> Files,
    IReadOnlyList<AgentDocsReadCliTotal> ByCli,
    string GeneratedAt);
