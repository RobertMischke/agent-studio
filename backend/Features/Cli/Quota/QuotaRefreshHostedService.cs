namespace AgentStudio.Cli;

/// <summary>
/// Refreshes CLI quota snapshots away from HTTP request paths. Each individual
/// probe is bounded by <see cref="QuotaService"/>, while this worker provides a
/// bounded cadence even when no browser is polling the cache endpoint.
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
        var seconds = Math.Clamp(
            configuration.GetValue<int?>("Quota:RefreshIntervalSeconds") ?? DefaultIntervalSeconds,
            30,
            24 * 60 * 60);
        _interval = TimeSpan.FromSeconds(seconds);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield startup first so cache hydration and HTTP serving are available
        // before any slow PTY process is created.
        await Task.Yield();
        await RefreshSafely(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RefreshSafely(stoppingToken);
        }
    }

    private async Task RefreshSafely(CancellationToken ct)
    {
        try
        {
            await _quota.RefreshAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Scheduled CLI quota refresh stopped with the host");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled CLI quota refresh failed; keeping the last-good cache");
        }
    }
}
