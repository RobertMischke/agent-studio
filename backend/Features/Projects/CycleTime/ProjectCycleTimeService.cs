using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

public sealed record ProjectCycleTimeCoverage(
    /// <summary>Non-fixture tasks of the project, archive-inclusive.</summary>
    int TasksInProject,
    /// <summary>Tasks with a usable completion timestamp (any time).</summary>
    int TasksCompleted,
    /// <summary>Tasks whose completion falls into the window; these form the aggregates and rows.</summary>
    int TasksInWindow,
    /// <summary>Terminal tasks without a completion timestamp (archived without a recorded 6-completed entry).</summary>
    int ExcludedNoCompletionTimestamp,
    /// <summary>Tasks not yet terminal; cycle time is defined on completion.</summary>
    int ExcludedInFlight,
    int ExcludedEpics,
    /// <summary>Window tasks whose ledger is missing; their lead time is reported as unattributed.</summary>
    int TasksWithoutLedger,
    /// <summary>Window tasks whose completion time came from the legacy lane-entry fallback.</summary>
    int TasksWithLaneEntryCompletion);

public sealed record ProjectCycleTimeResponse(
    string Project,
    string? ProjectId,
    string? ShortCode,
    string Window,
    DateTime CapturedAt,
    DateTime? Since,
    ProjectCycleTimeCoverage Coverage,
    /// <summary>Additive lane stages in lane order, then the rollups, then the counts.</summary>
    IReadOnlyList<CycleTimeStageAggregate> Aggregates,
    IReadOnlyList<CycleTimeOutcomeCount> IntegrationOutcomes,
    /// <summary>Per-task rows, newest completion first.</summary>
    IReadOnlyList<TaskCycleTime> Tasks);

/// <summary>
/// Read model for <c>GET /api/projects/{project}/cycle-time</c>. Enumerates the
/// project's tasks through the index-cache-backed scanner (never a cold disk
/// walk), analyses each terminal task once, and memoises the per-task row
/// against the ledger and pipeline file stamps so a request after warm-up
/// costs two <c>FileInfo</c> probes per task. The per-project result is
/// additionally cached for a short TTL.
/// </summary>
public sealed class ProjectCycleTimeService
{
    public const string DefaultWindow = "7d";
    public const string AllWindow = "all";
    private static readonly TimeSpan ProjectCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly Regex WindowPattern = new(@"^(\d{1,4})d$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly PipelineExecutionLog _pipeline;
    private readonly ProjectRegistry? _projects;
    private readonly ILogger<ProjectCycleTimeService> _logger;

    private readonly ConcurrentDictionary<string, TaskMemo> _taskMemo =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectMemo> _projectMemo =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectCycleTimeService(
        TaskScannerService scanner,
        TimelineLog timeline,
        PipelineExecutionLog pipeline,
        ILogger<ProjectCycleTimeService> logger,
        ProjectRegistry? projects = null)
    {
        _scanner = scanner;
        _timeline = timeline;
        _pipeline = pipeline;
        _logger = logger;
        _projects = projects;
    }

    /// <summary>Parses <c>7d</c>, <c>30d</c>, ..., or <c>all</c>. Returns null for an invalid value.</summary>
    public static bool TryParseWindow(string? raw, out string normalized, out TimeSpan? span)
    {
        normalized = DefaultWindow;
        span = TimeSpan.FromDays(7);
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var value = raw.Trim().ToLowerInvariant();
        if (value == AllWindow)
        {
            normalized = AllWindow;
            span = null;
            return true;
        }
        var match = WindowPattern.Match(value);
        if (!match.Success) return false;
        var days = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (days <= 0) return false;
        normalized = $"{days}d";
        span = TimeSpan.FromDays(days);
        return true;
    }

    /// <summary>Builds the response, or null when the project is unknown.</summary>
    public ProjectCycleTimeResponse? Build(string projectHandle, string? window, DateTime? nowUtc = null)
    {
        if (!TryParseWindow(window, out var normalizedWindow, out var span))
            throw new ArgumentException($"Invalid window '{window}'. Use 7d, 30d, or all.", nameof(window));

        var entry = ResolveWatchPath(projectHandle);
        if (entry is null) return null;
        var project = _projects?.FindByStorageLocation(entry.Path)
                      ?? _projects?.FindByIdOrDisplayName(projectHandle)
                      ?? _projects?.FindByShortCode(projectHandle);

        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        DateTime? since = span is null ? null : now - span.Value;

        var tasks = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, entry.Path))
            .ToList();

        var analyses = AnalyzeProject(entry.Name, tasks, since);
        return BuildResponse(entry.Name, project?.Id, project?.ShortCode, normalizedWindow, now, since, analyses);
    }

    /// <summary>Pure aggregation over analysed tasks; exposed for tests.</summary>
    internal static ProjectCycleTimeResponse BuildResponse(
        string projectName,
        string? projectId,
        string? shortCode,
        string window,
        DateTime now,
        DateTime? since,
        IReadOnlyList<TaskCycleAnalysis> analyses)
    {
        var completed = analyses.Where(a => a.Row is not null).Select(a => a.Row!).ToList();
        var rows = completed
            .Where(r => r.CompletedAt <= now.AddMinutes(5) && (since is null || r.CompletedAt >= since.Value))
            .OrderByDescending(r => r.CompletedAt)
            .ThenBy(r => r.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var coverage = new ProjectCycleTimeCoverage(
            analyses.Count,
            completed.Count + analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedBeforeWindow),
            rows.Count,
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedNoCompletion),
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedNotCompleted),
            analyses.Count(a => a.ExclusionReason == TaskCycleAnalysis.ExcludedEpic),
            rows.Count(r => r.DataGaps.Contains("no-ledger")),
            rows.Count(r => r.CompletionSource == "lane-entry"));

        var aggregates = new List<CycleTimeStageAggregate>();
        foreach (var stage in CycleTimeStages.Additive)
        {
            aggregates.Add(CycleTimeStatistics.Aggregate(stage, "stage", "seconds",
                rows.Select(r => r.Stages.Get(stage)).Where(v => v > 0)));
        }
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.ReviewRun, "rollup", "seconds",
            rows.Select(r => r.ReviewRunSeconds).Where(v => v > 0)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.LeadTime, "rollup", "seconds",
            rows.Select(r => r.LeadTimeSeconds).Where(v => v > 0)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.CycleTime, "rollup", "seconds",
            rows.Where(r => r.CycleTimeSeconds is > 0).Select(r => r.CycleTimeSeconds!.Value)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.CodingRuns, "count", "count",
            rows.Select(r => (double)r.CodingRuns)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.ReviewRounds, "count", "count",
            rows.Select(r => (double)r.ReviewRounds)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.BounceRounds, "count", "count",
            rows.Select(r => (double)r.BounceRounds)));
        aggregates.Add(CycleTimeStatistics.Aggregate(CycleTimeStages.IntegrationAttempts, "count", "count",
            rows.Select(r => (double)r.IntegrationAttempts)));

        var outcomes = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.IntegrationOutcome) ? "none" : r.IntegrationOutcome!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CycleTimeOutcomeCount(g.Key, g.Count()))
            .OrderByDescending(o => o.Count)
            .ThenBy(o => o.Outcome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectCycleTimeResponse(
            projectName, projectId, shortCode, window, now, since, coverage, aggregates, outcomes, rows);
    }

    private WatchPathEntry? ResolveWatchPath(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        var entries = _scanner.GetWatchPaths();
        var byName = entries.FirstOrDefault(e => string.Equals(e.Name, handle, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        var project = _projects?.FindByIdOrDisplayName(handle) ?? _projects?.FindByShortCode(handle);
        if (project is not null && !string.IsNullOrWhiteSpace(project.StorageLocation))
        {
            var byStorage = entries.FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, project.StorageLocation));
            if (byStorage is not null) return byStorage;
        }
        return entries.FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, handle));
    }

    private IReadOnlyList<TaskCycleAnalysis> AnalyzeProject(string projectName, List<TaskInfo> tasks, DateTime? since)
    {
        // A bounded window only needs tasks that could have completed inside
        // it. A task enters its terminal lane after completion, so
        // EnteredLaneAt is a safe lower bound for the completion time; older
        // terminal tasks are skipped without touching their files. Non-terminal
        // tasks are classified without any file read.
        var memoKey = $"{projectName}|{(since is null ? "all" : "window")}";
        if (_projectMemo.TryGetValue(memoKey, out var cached)
            && DateTime.UtcNow - cached.At < ProjectCacheTtl
            && cached.TaskCount == tasks.Count
            && (since is null || (cached.Since is not null && cached.Since <= since)))
        {
            // A slightly older window start is a superset; BuildResponse
            // applies the exact window to the rows.
            return cached.Analyses;
        }

        var results = new ConcurrentBag<TaskCycleAnalysis>();
        var work = new List<TaskInfo>();
        foreach (var task in tasks)
        {
            var terminal = string.Equals(task.State, TaskStates.Completed, StringComparison.Ordinal)
                           || string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal);
            if (!terminal)
            {
                results.Add(new TaskCycleAnalysis(null, string.Equals(task.Kind, TaskKinds.Epic, StringComparison.OrdinalIgnoreCase)
                    ? TaskCycleAnalysis.ExcludedEpic
                    : TaskCycleAnalysis.ExcludedNotCompleted));
                continue;
            }
            if (since is not null && task.EnteredLaneAt != default && task.EnteredLaneAt.ToUniversalTime() < since.Value)
            {
                // Completed before the window started: counts as completed for
                // coverage, never enters the rows. Keep the cheap classification.
                results.Add(new TaskCycleAnalysis(null, TaskCycleAnalysis.ExcludedBeforeWindow));
                continue;
            }
            work.Add(task);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) };
        Parallel.ForEach(work, options, task => results.Add(AnalyzeTask(task)));

        var analyses = results.ToList();
        _projectMemo[memoKey] = new ProjectMemo(DateTime.UtcNow, since, tasks.Count, analyses);
        return analyses;
    }

    private TaskCycleAnalysis AnalyzeTask(TaskInfo task)
    {
        var folder = task.FolderPath;
        if (string.IsNullOrWhiteSpace(folder))
            return TaskCycleTimeAnalyzer.Analyze(task, [], null);

        var timelineStamp = Stamp(TaskPaths.TimelineLog(folder));
        var pipelineStamp = Stamp(Path.Combine(folder, PipelineExecutionLog.FileName));
        if (_taskMemo.TryGetValue(folder, out var memo)
            && memo.TimelineStamp == timelineStamp
            && memo.PipelineStamp == pipelineStamp
            && memo.State == task.State
            && memo.EnteredLaneAt == task.EnteredLaneAt)
        {
            return memo.Analysis;
        }

        IReadOnlyList<TimelineEvent> events;
        try
        {
            events = _timeline.ReadAll(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "cycle-time-timeline-read-failed task={TaskId} folder={Folder}", task.Id, folder);
            events = [];
        }

        PipelineExecutionRecord? pipeline = null;
        try
        {
            pipeline = _pipeline.Read(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "cycle-time-pipeline-read-failed task={TaskId} folder={Folder}", task.Id, folder);
        }

        var analysis = TaskCycleTimeAnalyzer.Analyze(task, events, pipeline);
        _taskMemo[folder] = new TaskMemo(timelineStamp, pipelineStamp, task.State, task.EnteredLaneAt, analysis);
        return analysis;
    }

    private static (long Length, long Ticks) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-2, 0);
        }
    }

    private sealed record TaskMemo(
        (long Length, long Ticks) TimelineStamp,
        (long Length, long Ticks) PipelineStamp,
        string State,
        DateTime EnteredLaneAt,
        TaskCycleAnalysis Analysis);

    private sealed record ProjectMemo(DateTime At, DateTime? Since, int TaskCount, IReadOnlyList<TaskCycleAnalysis> Analyses);
}
