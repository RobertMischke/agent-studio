namespace AgentStudio.Tasks;

/// <summary>
/// Emits the task-index cache counters once per minute so invalidation churn
/// and the resulting refresh rate are visible in routine production logs.
/// </summary>
public sealed class TaskIndexCacheStatsService : BackgroundService
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);

    private readonly TaskIndexCache _cache;
    private readonly ILogger<TaskIndexCacheStatsService> _logger;

    public TaskIndexCacheStatsService(
        TaskIndexCache cache,
        ILogger<TaskIndexCacheStatsService> logger)
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

            LogRollup();
        }
    }

    internal void LogRollup()
    {
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
