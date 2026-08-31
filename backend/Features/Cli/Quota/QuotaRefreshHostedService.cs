namespace AgentStudio.Cli;

/// <summary>
/// Refreshes CLI quota snapshots away from the HTTP request path. The first
/// pass starts after host startup yields, then subsequent passes run at one
/// bounded interval without overlap. Individual probes keep their own hard
/// timeout in <see cref="QuotaService"/>.
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
        var configured = configuration.GetValue<int?>("Quota:RefreshIntervalSeconds")
            ?? configuration.GetValue<int?>("Quota:TtlSeconds")
            ?? DefaultIntervalSeconds;
        _interval = TimeSpan.FromSeconds(Math.Clamp(configured, 30, 24 * 60 * 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep CLI process startup off the host startup thread. Disk hydration
        // has already happened in QuotaService, so cache-only GETs are ready.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _quota.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled CLI quota refresh failed; retaining last-good snapshots");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
