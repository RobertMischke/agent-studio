namespace AgentStudio.Docs;

/// <summary>
/// Warms <see cref="WikiContentCache"/> for every watched project after the
/// host has started, and then logs a periodic counter rollup.
///
/// The warmup deliberately runs off the startup path: a full docs/ projection
/// of a large wiki (PROJ-002: ~700 files / 37 MB) costs seconds, and paying
/// that before the HTTP listener binds delays every other consumer of the
/// backend - including the health probe the update verifier waits on. Yielding
/// first hands control back to the host immediately; a request that beats the
/// warmup still gets a correct answer, it just pays one cold fill (and is
/// visible as a non-zero <c>misses</c> in the rollup).
/// </summary>
public sealed class WikiCacheWarmupService : BackgroundService
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(15);

    private readonly WikiContentCache _cache;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<WikiCacheWarmupService> _logger;

    public WikiCacheWarmupService(
        WikiContentCache cache,
        TaskScannerService scanner,
        ILogger<WikiCacheWarmupService> logger)
    {
        _cache = cache;
        _scanner = scanner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to the host before doing any filesystem work, so
        // StartAsync never awaits the warmup.
        await Task.Yield();

        WarmAllProjects(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StatsInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            LogRollup("periodic");
        }
    }

    internal void WarmAllProjects(CancellationToken ct)
    {
        var projects = ProjectNames();
        if (projects.Count == 0) return;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var warmed = 0;

        foreach (var projectName in projects)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                if (_cache.Preload(projectName)) warmed++;
            }
            catch (Exception ex)
            {
                // A single unreadable project must not stop the others; the
                // affected wiki simply fills lazily on its first request.
                _logger.LogWarning(ex, "wiki-cache-warmup-failed project={Project}", projectName);
            }
        }

        _logger.LogInformation(
            "wiki-cache-warmup-complete projects={Projects} warmed={Warmed} elapsedMs={ElapsedMs}",
            projects.Count,
            warmed,
            started.ElapsedMilliseconds);
        LogRollup("warmup");
    }

    private List<string> ProjectNames()
    {
        try
        {
            return _scanner.GetWatchPaths()
                .Select(entry => entry.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "wiki-cache-warmup could not enumerate watch paths");
            return [];
        }
    }

    private void LogRollup(string reason)
    {
        var stats = _cache.GetStats();
        _logger.LogInformation(
            "wiki-cache-stats reason={Reason} projects={Projects} hits={Hits} misses={Misses} "
                + "fills={Fills} preloads={Preloads} watcherInvalidations={WatcherInvalidations} "
                + "mutationInvalidations={MutationInvalidations} fillMsTotal={FillMsTotal}",
            reason,
            stats.Projects,
            stats.Hits,
            stats.Misses,
            stats.Fills,
            stats.Preloads,
            stats.WatcherInvalidations,
            stats.MutationInvalidations,
            stats.FillMsTotal);
    }
}
