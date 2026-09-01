namespace AgentStudio.Tasks;

/// <summary>
/// Emits a periodic rollup of task-index cache activity so invalidation churn
/// and its effect on refresh frequency are visible in routine runtime logs.
/// </summary>
public sealed class TaskIndexCacheDiagnosticsService : BackgroundService
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMinutes(1);

    private readonly TaskIndexCache _cache;
    private readonly ILogger<TaskIndexCacheDiagnosticsService> _logger;

    public TaskIndexCacheDiagnosticsService(
        TaskIndexCache cache,
        ILogger<TaskIndexCacheDiagnosticsService> logger)
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
