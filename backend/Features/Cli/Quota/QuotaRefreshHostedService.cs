namespace AgentStudio.Cli;

/// <summary>
/// Refreshes quota snapshots on a backend-owned bounded cadence. The public GET
/// remains cache-only; this worker and demand-triggered stale refreshes share
/// the same per-CLI coalescing and timeout inside <see cref="QuotaService"/>.
/// </summary>
public sealed class QuotaRefreshHostedService : BackgroundService
{
    public const int DefaultIntervalSeconds = 600;

    private readonly QuotaService _quota;
    private readonly TimeSpan _interval;
    private readonly ILogger<QuotaRefreshHostedService> _logger;

    public QuotaRefreshHostedService(
        QuotaService quota,
        IConfiguration configuration,
        ILogger<QuotaRefreshHostedService> logger)
    {
        _quota = quota;
        _logger = logger;
        var seconds = configuration.GetValue<int?>("Quota:RefreshIntervalSeconds")
            ?? DefaultIntervalSeconds;
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 24 * 60 * 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await _quota.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled quota refresh failed; retaining last-good readings");
            }
        }
    }
}
