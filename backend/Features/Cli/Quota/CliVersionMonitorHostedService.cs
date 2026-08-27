namespace AgentStudio.Cli;

/// <summary>
/// Reads Claude and Codex versions after startup and on a bounded periodic
/// cadence. The tracker compares the live value with the disk-cached quota
/// baseline, making a version change visible even before the next quota parse.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 10;

    private readonly CliRouter _router;
    private readonly LocalCliSelfHealService _selfHeal;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;

    public CliVersionMonitorHostedService(
        CliRouter router,
        LocalCliSelfHealService selfHeal,
        QuotaService quotaService,
        IConfiguration configuration,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        _router = router;
        _selfHeal = selfHeal;
        _configuration = configuration;
        _logger = logger;
        // Resolving QuotaService hydrates the tracker from the disk cache
        // before the startup comparison reads the live binaries.
        _ = quotaService;
    }

    internal async Task CheckOnceAsync(string source, CancellationToken ct = default)
    {
        try
        {
            await _selfHeal.ProbeAllAsync(_router, source, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local CLI capability check failed; the next periodic check will retry");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not run synchronous process probes on the host startup thread.
        await Task.Yield();
        await CheckOnceAsync("startup", stoppingToken);

        var minutes = Math.Clamp(
            _configuration.GetValue<int?>("CliVersionMonitor:IntervalMinutes")
                ?? DefaultIntervalMinutes,
            1,
            24 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            await CheckOnceAsync("periodic", stoppingToken);
        }
    }
}
