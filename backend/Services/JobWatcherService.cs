using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class JobWatcherService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JobWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];

    public event Action<string>? OnJobChanged;

    public JobWatcherService(IConfiguration config, ILogger<JobWatcherService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = _config.GetSection("WatchPaths").Get<List<string>>() ?? [];

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                _logger.LogWarning("Watch path does not exist, creating: {Path}", path);
                Directory.CreateDirectory(path);
            }

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, e) => Debounce(e.FullPath);
            watcher.Created += (_, e) => Debounce(e.FullPath);
            watcher.Deleted += (_, e) => Debounce(e.FullPath);
            watcher.Renamed += (_, e) => Debounce(e.FullPath);

            _watchers.Add(watcher);
            _logger.LogInformation("Watching: {Path}", path);
        }

        stoppingToken.Register(() =>
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        });

        return Task.CompletedTask;
    }

    private DateTime _lastEvent = DateTime.MinValue;
    private readonly Lock _lock = new();

    private void Debounce(string path)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastEvent).TotalMilliseconds < 500) return;
            _lastEvent = now;
        }

        _logger.LogDebug("File change detected: {Path}", path);
        // FileSystemWatcher fires this on a thread-pool callback. An
        // unhandled exception escaping a subscriber here goes through
        // AppDomain.UnhandledException and terminates the process, which is
        // the silent-kill class we are guarding against. Log and swallow so
        // a single bad subscriber cannot crash the host.
        try { OnJobChanged?.Invoke(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnJobChanged subscriber threw for {Path}", path); }
    }
}
