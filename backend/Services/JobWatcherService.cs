using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services;

/// <summary>
/// Watches the resolved job-folder paths configured under
/// <c>WatchPaths</c> and fans out a single <see cref="OnJobChanged"/>
/// event whenever something inside a job folder appears, disappears, or
/// changes on disk. Subscribers include
/// <see cref="JobIndexCache.Invalidate"/> (Cycle 1 cache invalidation),
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
/// <see cref="JobScannerService.GetWatchPaths"/> (which already honours
/// the <c>.orchestrator.yml</c> pointer flow) and watching the resolved
/// <c>entry.Path</c>.</para>
/// </summary>
public class JobWatcherService : BackgroundService
{
    private readonly JobScannerService _scanner;
    private readonly ILogger<JobWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly TimeSpan _debounce;

    public event Action<string>? OnJobChanged;

    public JobWatcherService(JobScannerService scanner, ILogger<JobWatcherService> logger, IConfiguration config)
    {
        _scanner = scanner;
        _logger = logger;
        var debounceMs = int.TryParse(config["JobWatcher:DebounceMs"], out var v) ? v : 250;
        _debounce = TimeSpan.FromMilliseconds(Math.Max(50, debounceMs));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entries = _scanner.GetWatchPaths();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                _logger.LogWarning("WatchPath '{Name}' resolved to empty path; skipping watcher", entry.Name);
                continue;
            }
            if (!Directory.Exists(entry.Path))
            {
                _logger.LogWarning("Watch path does not exist, creating: {Path}", entry.Path);
                try { Directory.CreateDirectory(entry.Path); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create watch path {Path}; skipping watcher", entry.Path);
                    continue;
                }
            }

            try
            {
                var watcher = new FileSystemWatcher(entry.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 64 * 1024
                };

                watcher.Changed += (_, e) => Debounce(e.FullPath);
                watcher.Created += (_, e) => Debounce(e.FullPath);
                watcher.Deleted += (_, e) => Debounce(e.FullPath);
                watcher.Renamed += (_, e) => Debounce(e.FullPath);
                watcher.Error += (_, e) =>
                    _logger.LogWarning(e.GetException(), "FileSystemWatcher error for {Path}", entry.Path);

                _watchers.Add(watcher);
                _logger.LogInformation("Watching: {Name} -> {Path}", entry.Name, entry.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start watcher for {Path}", entry.Path);
            }
        }

        stoppingToken.Register(() =>
        {
            foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
        });

        return Task.CompletedTask;
    }

    private DateTime _lastEvent = DateTime.MinValue;
    private readonly Lock _lock = new();

    /// <summary>
    /// Coalesces bursty FileSystemWatcher events into a single
    /// <see cref="OnJobChanged"/> invocation per debounce window. Also
    /// filters out paths that obviously don't represent a job-state change
    /// (orchestrator log churn, attachment binary writes) so cache
    /// invalidations don't fire on every CLI heartbeat.
    /// </summary>
    private void Debounce(string path)
    {
        if (IsNoiseyPath(path)) return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastEvent < _debounce) return;
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
    /// Returns true when the path obviously doesn't represent a job lane
    /// change worth notifying about. Lives here so the noise filter is in
    /// one place; downstream subscribers (JobIndexCache, runner
    /// reconciliation, SignalR push) all benefit from the same gate.
    /// </summary>
    private static bool IsNoiseyPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        // Orchestrator log files and chat attachments churn constantly during
        // a run. The runner's active job stays in 3-progress for the whole
        // run, so cache invalidation on every log line is pure waste.
        var p = path.Replace('\\', '/');
        if (p.Contains("/.orchestrator/")) return true;
        if (p.Contains("/chat/")) return true;
        if (p.Contains("/attachments/")) return true;
        if (p.Contains("/results/")) return true;
        // CLI streams write tool-calls.jsonl, cli-output.log, session-events.jsonl
        // continuously. They live under <jobDir>/logs/ and never affect the
        // lane / job.json fields the cache tracks.
        if (p.Contains("/logs/")) return true;
        // VS Code, ripgrep, and other tooling create temp files on the way
        // to atomic writes. Ignore the obvious patterns.
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') && (name.EndsWith(".tmp") || name.EndsWith(".swp"))) return true;
        if (name.EndsWith("~")) return true;
        return false;
    }
}
