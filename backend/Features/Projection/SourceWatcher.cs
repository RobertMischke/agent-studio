using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Projection;

/// <summary>
/// Bridges the existing per-watch-path <see cref="TaskWatcherService"/> into
/// the conversation projector. When a job's <c>logs/cli-output.log</c>
/// changes on disk, this service debounces (300 ms per job) and asks the
/// projector to re-render and broadcast a delta over the SignalR hub.
///
/// Disabled when <c>ConversationProjection:BackendEnabled</c> is false so
/// the file-watch path matches the rest of the feature-flagged surface.
/// </summary>
public sealed class SourceWatcher : IHostedService, IDisposable
{
    private readonly TaskWatcherService _watcher;
    private readonly ConversationProjector _projector;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<SourceWatcher> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);
    private readonly ConcurrentDictionary<string, DateTime> _lastFiredByJobId = new(StringComparer.Ordinal);
    private Action<string>? _subscription;

    public SourceWatcher(
        TaskWatcherService watcher,
        ConversationProjector projector,
        TaskScannerService scanner,
        ILogger<SourceWatcher> logger,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _watcher = watcher;
        _projector = projector;
        _scanner = scanner;
        _logger = logger;
        _enabled = bool.TryParse(config["ConversationProjection:BackendEnabled"], out var v) && v;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return Task.CompletedTask;

        _subscription = path =>
        {
            try { OnPathChanged(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "SourceWatcher dispatch failed for {Path}", path); }
        };
        _watcher.OnJobChanged += _subscription;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            _watcher.OnJobChanged -= _subscription;
            _subscription = null;
        }
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    private void OnPathChanged(string path)
    {
        if (!path.EndsWith("cli-output.log", StringComparison.OrdinalIgnoreCase)) return;

        // The path looks like .../jobs-root/<lane>/<slug>/logs/cli-output.log.
        // The slug is the directory two levels up from the file.
        var logsDir = Path.GetDirectoryName(path);
        if (logsDir is null) return;
        var jobDir = Path.GetDirectoryName(logsDir);
        if (jobDir is null) return;
        var slug = Path.GetFileName(jobDir);
        if (string.IsNullOrWhiteSpace(slug)) return;

        var now = DateTime.UtcNow;
        var last = _lastFiredByJobId.GetOrAdd(slug, DateTime.MinValue);
        if (now - last < _debounce) return;
        _lastFiredByJobId[slug] = now;

        var info = _scanner.FindJob(slug);
        if (info is null) return;

        _ = Task.Run(async () =>
        {
            try { await _projector.ProjectAndBroadcastAsync(info, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProjectAndBroadcastAsync failed for {Slug}", slug); }
        });
    }
}
