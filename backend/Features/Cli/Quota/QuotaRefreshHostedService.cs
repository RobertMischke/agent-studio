namespace AgentStudio.Cli;

/// <summary>
/// Refreshes the durable quota cache independently of API traffic. Each pass
/// waits for the previous one to finish, while <see cref="QuotaService"/>
/// applies a bounded timeout to every CLI probe.
/// </summary>
public sealed class QuotaRefreshHostedService : BackgroundService
{
    private readonly QuotaService _quota;
    private readonly ILogger<QuotaRefreshHostedService> _logger;
    private readonly TimeSpan _interval;

    public QuotaRefreshHostedService(
        QuotaService quota,
        IConfiguration configuration,
        ILogger<QuotaRefreshHostedService> logger)
    {
        _quota = quota;
        _logger = logger;
        var configuredSeconds = int.TryParse(configuration["Quota:RefreshIntervalSeconds"], out var seconds)
            ? seconds
            : (int)quota.Ttl.TotalSeconds;
        _interval = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 30, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafely(stoppingToken);
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshSafely(stoppingToken);
        }
    }

    private async Task RefreshSafely(CancellationToken stoppingToken)
    {
        try
        {
            await _quota.RefreshAllAsync(stoppingToken);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "QuotaRefreshHostedService: normal host shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled quota refresh failed; retaining last-good values");
        }
    }
}
