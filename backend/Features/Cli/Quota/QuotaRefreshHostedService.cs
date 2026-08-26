namespace AgentStudio.Cli;

/// <summary>
/// Starts quota revalidation at boot and checks periodically thereafter.
/// <see cref="QuotaService.GetWithBackgroundRefresh"/> only queues bounded
/// probes and returns cached values, so this loop never holds startup or an API
/// request open while a CLI TUI renders.
/// </summary>
public sealed class QuotaRefreshHostedService : BackgroundService
{
    private readonly QuotaService _quota;
    private readonly ILogger<QuotaRefreshHostedService> _logger;

    public QuotaRefreshHostedService(
        QuotaService quota,
        ILogger<QuotaRefreshHostedService> logger)
    {
        _quota = quota;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before the first CLI process is touched so BackgroundService
        // startup itself remains non-blocking.
        await Task.Yield();

        var cadence = TimeSpan.FromSeconds(Math.Clamp(_quota.Ttl.TotalSeconds / 4, 30, 120));
        var startup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (startup)
                {
                    startup = false;
                    await _quota.RefreshAllAsync(stoppingToken);
                }
                else
                {
                    _quota.GetWithBackgroundRefresh();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Periodic quota refresh scheduling failed");
            }

            try
            {
                await Task.Delay(cadence, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
