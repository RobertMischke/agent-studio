using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Watches the resolved job-folder paths configured under
/// <c>WatchPaths</c> and fans out a single <see cref="OnJobChanged"/>
/// event whenever something inside a job folder appears, disappears, or
/// changes on disk. Subscribers include
/// <see cref="TaskIndexCache.Invalidate"/> (Cycle 1 cache invalidation),
/// the SignalR hub (broadcasts <c>jobsChanged</c> to clients), and
/// <see cref="TaskRunnerService.ReconcileAllRunners"/> (releases the
/// runner's active-job latch when the folder leaves <c>3-progress</c>
/// outside the API).
///
/// <para><b>Pre-Cycle-1 history:</b> this service used to read
/// <c>WatchPaths</c> as <c>List&lt;string&gt;</c> while the actual config
/// schema is <c>List&lt;WatchPathEntry&gt;</c>; the cast returned an
/// empty list, no <see cref="FileSystemWatcher"/> was ever constructed,
/// and <see cref="OnJobChanged"/> never fired. SignalR <c>jobsChanged</c>
/// pushes and runner reconciliation were dead silent against external
/// folder moves. Cycle 1 fixes the config read by delegating to
/// <see cref="TaskScannerService.GetWatchPaths"/> (which already honours
/// the <c>.orchestrator.yml</c> pointer flow) and watching the resolved
/// <c>entry.Path</c>.</para>
/// </summary>
public class TaskWatcherService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<TaskWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, string> _taskIndexSignatures = new(PathComparer);
    private DateTime? _startedAt;
    private int _configuredPathCount;
    private string? _lastError;

    public event Action<string>? OnJobChanged;
    /// <summary>
    /// Raw filesystem change stream for consumers that own their own narrow
    /// filtering (for example conversation projection of cli-output.log).
    /// Unlike <see cref="OnJobChanged"/>, this event never invalidates the
    /// task index by itself.
    /// </summary>
    public event Action<string>? OnPathChanged;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public TaskWatcherService(TaskScannerService scanner, ILogger<TaskWatcherService> logger, IConfiguration config)
    {
        _scanner = scanner;
        _logger = logger;
        var debounceMs = int.TryParse(config["TaskWatcher:DebounceMs"], out var v) ? v : 250;
        _debounce = TimeSpan.FromMilliseconds(Math.Max(50, debounceMs));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entries = _scanner.GetWatchPaths();
        lock (_lock)
        {
            _startedAt = DateTime.UtcNow;
            _configuredPathCount = entries.Count;
            _lastError = null;
        }
        foreach (var entry in entries)
        {
            EnsureWatching(entry);
        }
        stoppingToken.Register(() =>
        {
            FileSystemWatcher[] watchers;
            lock (_lock)
            {
                watchers = _watchers.ToArray();
                _watchers.Clear();
            }
            foreach (var watcher in watchers)
            {
                try { watcher.Dispose(); }
                catch (Exception __ex) { SilentCatch.Note(__ex, "TaskWatcherService: watcher dispose"); }
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Add a live watcher for an API-created project.</summary>
    public bool EnsureWatching(WatchPathEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Path))
        {
            _logger.LogWarning("WatchPath '{Name}' resolved to empty path; skipping watcher", entry.Name);
            return false;
        }

        lock (_lock)
        {
            if (_watchers.Any(w => string.Equals(w.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        try
        {
            Directory.CreateDirectory(entry.Path);
            var watcher = new FileSystemWatcher(entry.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Created += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Deleted += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType);
            watcher.Renamed += (_, e) => HandleChange(entry.Path, e.FullPath, e.ChangeType, e.OldFullPath);
            watcher.Error += (_, e) =>
            {
                var error = e.GetException();
                lock (_lock) _lastError = error?.Message ?? "FileSystemWatcher reported an unknown error.";
                _logger.LogWarning(error, "FileSystemWatcher error for {Path}", entry.Path);
            };
            lock (_lock)
            {
                _watchers.Add(watcher);
                _configuredPathCount = Math.Max(_configuredPathCount, _watchers.Count);
            }
            _logger.LogInformation("watch-path-activated project={Name} path={Path}", entry.Name, entry.Path);
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            _logger.LogError(ex, "Failed to start watcher for {Path}", entry.Path);
            return false;
        }
    }

    /// <summary>
    /// Cheap read-only watcher health used by the orchestrator context digest.
    /// It deliberately reports the actual active handle count and last observed
    /// event/error instead of inferring health from the static <c>/healthz</c>
    /// response. No paths are exposed in the snapshot.
    /// </summary>
    public TaskWatcherHealthSnapshot GetHealthSnapshot()
    {
        lock (_lock)
        {
            var active = _watchers.Count;
            return new TaskWatcherHealthSnapshot(
                StartedAt: _startedAt,
                ConfiguredPathCount: _configuredPathCount,
                ActiveWatcherCount: active,
                LastEventAt: _lastEvent == DateTime.MinValue ? null : _lastEvent,
                LastError: _lastError,
                Healthy: _startedAt != null
                    && string.IsNullOrWhiteSpace(_lastError)
                    && (_configuredPathCount == 0 || active == _configuredPathCount));
        }
    }

    private DateTime _lastEvent = DateTime.MinValue;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTime> _lastNotifiedByTask = new(PathComparer);

    private void HandleChange(
        string watchPath,
        string path,
        WatcherChangeTypes changeType,
        string? oldPath = null)
    {
        try { OnPathChanged?.Invoke(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnPathChanged subscriber threw for {Path}", path); }

        if (!ShouldNotifyIndexChange(watchPath, path, changeType, oldPath)) return;
        Debounce(path);
    }

    /// <summary>
    /// Coalesces bursty FileSystemWatcher events into a single
    /// <see cref="OnJobChanged"/> invocation per task and debounce window.
    /// Filtering happens before this method so generated sidecars never enter
    /// the task-index invalidation path.
    /// </summary>
    private void Debounce(string path)
    {
        if (IsNoiseyPath(path)) return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var key = string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path) ?? path
                : path;
            if (_lastNotifiedByTask.TryGetValue(key, out var last) && now - last < _debounce) return;
            _lastNotifiedByTask[key] = now;
            _lastEvent = now;
        }

        _logger.LogDebug("Watcher fired for {Path}", path);
        // FileSystemWatcher delivers callbacks on the thread pool. An
        // unhandled exception escaping a subscriber goes through
        // AppDomain.UnhandledException and terminates the host - the
        // silent-kill class we are guarding against. Log and swallow so a
        // single bad subscriber cannot crash the process.
        try { OnJobChanged?.Invoke(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnJobChanged subscriber threw for {Path}", path); }
    }

    /// <summary>
    /// Decides whether a filesystem event can change a cached
    /// <see cref="TaskInfo"/>. Only task.json content and task-folder
    /// structural moves/deletes qualify. Generated sidecars, logs, results,
    /// lifecycle output and index mirrors are intentionally excluded: API
    /// writers invalidate synchronously, while genuinely external edits are
    /// covered by the safety TTL.
    ///
    /// A task.json-only <c>lastProgressAt</c> heartbeat is also excluded after
    /// its first observation. That field is crash-recovery metadata and is not
    /// projected into <see cref="TaskInfo"/>; treating every heartbeat as a
    /// board mutation caused a full workspace scan feedback loop.
    /// </summary>
    internal bool ShouldNotifyIndexChange(
        string watchPath,
        string path,
        WatcherChangeTypes changeType,
        string? oldPath = null)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(path)) return false;

        if (string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase))
        {
            if (changeType == WatcherChangeTypes.Deleted)
            {
                _taskIndexSignatures.TryRemove(path, out _);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(oldPath)
                && !string.Equals(oldPath, path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                _taskIndexSignatures.TryRemove(oldPath, out _);
            }

            var signature = ComputeTaskIndexSignature(path);
            if (signature == null) return true; // partial/locked write: fail open
            if (_taskIndexSignatures.TryGetValue(path, out var previous)
                && string.Equals(previous, signature, StringComparison.Ordinal))
                return false;
            _taskIndexSignatures[path] = signature;
            return true;
        }

        if (changeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed)
            return IsTaskFolderPath(watchPath, path)
                   || (!string.IsNullOrWhiteSpace(oldPath) && IsTaskFolderPath(watchPath, oldPath));

        return false;
    }

    internal static string? ComputeTaskIndexSignature(string taskJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(taskJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var signature = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "lastProgressAt", StringComparison.OrdinalIgnoreCase))
                    continue;
                signature.Append(property.Name.Length).Append(':').Append(property.Name)
                    .Append('=').Append(property.Value.GetRawText()).Append(';');
            }
            return signature.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTaskFolderPath(string watchPath, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(watchPath, path);
            if (relative == "." || Path.IsPathRooted(relative)) return false;
            var parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part == "..")) return false;

            // Legacy: <root>/<lane>/<slug>. Flat: <root>/tasks/<bucket>/<key>.
            return parts.Length == 2 && TaskStates.All.Contains(parts[0], StringComparer.Ordinal)
                   || parts.Length == 3
                   && string.Equals(parts[0], TaskStorageLayout.JobsDirName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Read-only health projection for <see cref="TaskWatcherService"/>.</summary>
public sealed record TaskWatcherHealthSnapshot(
    DateTime? StartedAt,
    int ConfiguredPathCount,
    int ActiveWatcherCount,
    DateTime? LastEventAt,
    string? LastError,
    bool Healthy);
