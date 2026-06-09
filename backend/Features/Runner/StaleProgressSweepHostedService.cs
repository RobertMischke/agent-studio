namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Periodic safety net for stale <c>3-progress</c> folders. The boot sweep
/// catches zombies present at startup; this service keeps the same
/// idempotent archiver running while the backend stays up, so a task that
/// crosses the resume-window threshold during uptime is requeued without a
/// restart.
/// </summary>
public sealed class StaleProgressSweepHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 5;

    private readonly StaleProgressArchiver _archiver;
    private readonly IConfiguration _config;
    private readonly ILogger<StaleProgressSweepHostedService> _logger;

    public StaleProgressSweepHostedService(
        StaleProgressArchiver archiver,
        IConfiguration config,
        ILogger<StaleProgressSweepHostedService> logger)
    {
        _archiver = archiver;
        _config = config;
        _logger = logger;
    }

    public Task<IReadOnlyList<StaleProgressDecision>> RunOnceAsync(CancellationToken ct = default)
        => _archiver.SweepAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stale-progress periodic sweep failed");
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = _config.GetValue<int?>("Supervisor:StaleProgressSweepIntervalMinutes") ?? DefaultIntervalMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }
}
