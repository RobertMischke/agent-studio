using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Tasks;

/// <summary>
/// Cache-only Git enrichment for task-list responses. List requests return the
/// latest completed projection immediately and may queue one detached refresh.
/// Git processes run only inside that background refresh, never on the request
/// execution context.
/// </summary>
public sealed class TaskListGitProjectionCache
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<TaskListGitProjectionCache> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IReadOnlyCollection<TaskInfo>, TaskListGitProjection> _refreshProjection;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);

    public TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        ILogger<TaskListGitProjectionCache> logger)
        : this(
            mergeStatus,
            integrationStatus,
            publishStatus,
            testRuns,
            logger,
            TimeProvider.System)
    {
    }

    internal TaskListGitProjectionCache(
        BoardMergeStatusService mergeStatus,
        TaskIntegrationStatusService integrationStatus,
        TaskPublishableService publishStatus,
        TestRunService testRuns,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider)
        : this(
            tasks =>
            {
                // The four lookups read independent projections (merge/publish
                // reachability sets, integration status, test-run evidence) and
                // are safe to fan out; each is already versioned/cached
                // internally, so this only shortens wall time on a real cache
                // miss instead of summing four sequential git-bound calls.
                var mergeTask = Task.Run(() => mergeStatus.BuildLookup(tasks));
                var integrationTask = Task.Run(() => integrationStatus.BuildLookup(tasks));
                var publishTask = Task.Run(() => publishStatus.BuildLookup(tasks));
                var testRunTask = Task.Run(() => testRuns.BuildLookup(tasks));
                Task.WhenAll(mergeTask, integrationTask, publishTask, testRunTask)
                    .GetAwaiter()
                    .GetResult();
                return new TaskListGitProjection(
                    mergeTask.Result,
                    integrationTask.Result,
                    publishTask.Result,
                    testRunTask.Result);
            },
            logger,
            timeProvider)
    {
    }

    internal TaskListGitProjectionCache(
        Func<IReadOnlyCollection<TaskInfo>, TaskListGitProjection> refreshProjection,
        ILogger<TaskListGitProjectionCache> logger,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _refreshProjection = refreshProjection;
    }

    /// <summary>
    /// Returns the most recently completed projection for the requested watch
    /// path set. A cold read returns empty enrichment and queues the initial
    /// refresh. The method performs no Git operation and never waits for an
    /// in-flight refresh.
    /// </summary>
    public TaskListGitProjection ReadCacheOnly(IReadOnlyCollection<TaskInfo> tasks)
    {
        if (tasks.Count == 0) return TaskListGitProjection.Empty;

        var captured = tasks.ToArray();
        var scopeKey = ScopeKey(captured);
        var signature = InputSignature(captured);
        var entry = _entries.GetOrAdd(scopeKey, static _ => new CacheEntry());
        TaskListGitProjection snapshot;
        var queueRefresh = false;

        lock (entry.Gate)
        {
            snapshot = entry.Snapshot;
            var now = _timeProvider.GetUtcNow();
            if (TaskListGitRefreshPolicy.ShouldQueue(
                    entry.HasSnapshot,
                    entry.Refreshing,
                    entry.InputSignature != signature,
                    entry.RefreshAfter <= now))
            {
                entry.Refreshing = true;
                queueRefresh = true;
            }
        }

        if (queueRefresh) QueueRefresh(scopeKey, entry, captured, signature);
        return snapshot;
    }

    private void QueueRefresh(
        string scopeKey,
        CacheEntry entry,
        TaskInfo[] tasks,
        int signature)
    {
        try
        {
            // The request's GitProcessTelemetry scope uses AsyncLocal. Suppress
            // ExecutionContext flow so background Git work cannot be charged to
            // the request after ReadCacheOnly has returned.
            if (ExecutionContext.IsFlowSuppressed())
            {
                _ = Task.Run(() => Refresh(scopeKey, entry, tasks, signature));
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(() => Refresh(scopeKey, entry, tasks, signature));
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Refreshing = false;
                entry.RefreshAfter = _timeProvider.GetUtcNow().Add(FailureRetryInterval);
            }
            _logger.LogWarning(ex, "Task-list Git projection refresh could not be queued for scope {Scope}.", scopeKey);
        }
    }

    private void Refresh(
        string scopeKey,
        CacheEntry entry,
        IReadOnlyCollection<TaskInfo> tasks,
        int signature)
    {
        var stopwatch = Stopwatch.StartNew();
        TaskListGitProjection? refreshed = null;
        try
        {
            using var telemetry = GitProcessTelemetry.BeginRequest(
                "tasks/list-refresh",
                _logger,
                includeNested: true);
            refreshed = _refreshProjection(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task-list Git projection refresh failed for scope {Scope}.", scopeKey);
        }
        finally
        {
            stopwatch.Stop();
            lock (entry.Gate)
            {
                if (refreshed is not null)
                {
                    entry.Snapshot = refreshed;
                    entry.HasSnapshot = true;
                    entry.InputSignature = signature;
                }
                entry.Refreshing = false;
                entry.RefreshAfter = _timeProvider.GetUtcNow().Add(
                    refreshed is null ? FailureRetryInterval : RefreshInterval);
            }
        }

        if (refreshed is not null)
        {
            _logger.LogInformation(
                "task-list-git-refresh-complete scope={Scope} tasks={TaskCount} elapsedMs={ElapsedMs}",
                scopeKey,
                tasks.Count,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static string ScopeKey(IEnumerable<TaskInfo> tasks)
        => string.Join(
            "|",
            tasks.Select(task => NormalizePath(task.WatchPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

    private static int InputSignature(IEnumerable<TaskInfo> tasks)
    {
        var hash = new HashCode();
        foreach (var task in tasks.OrderBy(task => task.TaskKey, StringComparer.Ordinal))
        {
            hash.Add(task.TaskKey, StringComparer.Ordinal);
            hash.Add(task.State, StringComparer.Ordinal);
            hash.Add(task.ProjectName, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.WatchPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.IntegrationBranch, StringComparer.Ordinal);
            hash.Add(task.Commit?.Sha, StringComparer.OrdinalIgnoreCase);
            hash.Add(task.Provenance?.Branch, StringComparer.Ordinal);
            hash.Add(task.Provenance?.Merge?.MergeCommit, StringComparer.OrdinalIgnoreCase);
            foreach (var commit in task.Commits)
            {
                hash.Add(commit.Sha, StringComparer.OrdinalIgnoreCase);
                hash.Add(commit.Branch, StringComparer.Ordinal);
                hash.Add(commit.FilesChanged);
            }
        }
        return hash.ToHashCode();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "TaskListGitProjectionCache: invalid watch path");
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }

    private sealed class CacheEntry
    {
        public object Gate { get; } = new();
        public TaskListGitProjection Snapshot { get; set; } = TaskListGitProjection.Empty;
        public bool HasSnapshot { get; set; }
        public bool Refreshing { get; set; }
        public int InputSignature { get; set; }
        public DateTimeOffset RefreshAfter { get; set; } = DateTimeOffset.MinValue;
    }
}

internal static class TaskListGitRefreshPolicy
{
    internal static bool ShouldQueue(
        bool hasSnapshot,
        bool refreshing,
        bool inputChanged,
        bool refreshDue)
        => !refreshing && (!hasSnapshot || inputChanged || refreshDue);
}

public sealed record TaskListGitProjection(
    IReadOnlyDictionary<string, TaskMergeSignal> Merge,
    IReadOnlyDictionary<string, TaskIntegrationStatus> Integration,
    IReadOnlyDictionary<string, TaskPublishSignal> Publish,
    IReadOnlyDictionary<string, TaskTestRunEvidence> TestRuns)
{
    public static TaskListGitProjection Empty { get; } = new(
        new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
        new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
        new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal));
}
