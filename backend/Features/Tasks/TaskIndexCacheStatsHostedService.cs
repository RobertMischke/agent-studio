namespace AgentStudio.Tasks;

/// <summary>
/// Logs a periodic counter rollup for <see cref="TaskIndexCache"/> so an
/// invalidation-churn regression (e.g. a tick-path guard that stopped
/// short-circuiting no-op writes) is visible in one minute of production log
/// instead of requiring a profiler. Mirrors the wiki-cache-stats rollup in
/// <see cref="AgentStudio.Docs.WikiCacheWarmupService"/>.
/// </summary>
public sealed class TaskIndexCacheStatsHostedService : BackgroundService
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);

    private readonly TaskIndexCache _cache;
    private readonly ILogger<TaskIndexCacheStatsHostedService> _logger;

    public TaskIndexCacheStatsHostedService(TaskIndexCache cache, ILogger<TaskIndexCacheStatsHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            var stats = _cache.GetStats();
            _logger.LogInformation(
                "task-index-cache-stats hits={Hits} misses={Misses} staleHits={StaleHits} "
                    + "externalInvalidations={ExternalInvalidations} mutationInvalidations={MutationInvalidations}",
                stats.Hits,
                stats.Misses,
                stats.StaleHits,
                stats.ExternalInvalidations,
                stats.MutationInvalidations);
        }
    }
}
