namespace AgentStudio.Cli;

/// <summary>
/// Refreshes quota snapshots independently of HTTP traffic. Each underlying
/// CLI probe has its own timeout in <see cref="QuotaService"/>; this service
/// only owns the bounded cadence and keeps failures away from the host loop.
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
        var seconds = Math.Clamp(
            configuration.GetValue<int?>("Quota:RefreshIntervalSeconds") ?? DefaultIntervalSeconds,
            30,
            24 * 60 * 60);
        _interval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await RefreshSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            await RefreshSafelyAsync(stoppingToken);
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken stoppingToken)
    {
        try { await _quota.RefreshAllAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Scheduled quota refresh stopped with the host");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled quota refresh failed; retaining last-good values");
        }
    }
}
