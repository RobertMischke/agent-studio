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
    private readonly CliVersionTracker _tracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;

    public CliVersionMonitorHostedService(
        CliRouter router,
        CliVersionTracker tracker,
        QuotaService quotaService,
        IConfiguration configuration,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        _router = router;
        _tracker = tracker;
        _configuration = configuration;
        _logger = logger;
        // Resolving QuotaService hydrates the tracker from the disk cache
        // before the startup comparison reads the live binaries.
        _ = quotaService;
    }

    internal void CheckOnce(string source)
    {
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            try
            {
                var probe = _router.Get(cliType).TestCliPath();
                if (probe.Available) _tracker.Observe(cliType, probe.Version, source);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI version check failed for {Cli}; the next periodic check will retry", cliType);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not run synchronous process probes on the host startup thread.
        await Task.Yield();
        CheckOnce("startup");

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
            CheckOnce("periodic");
        }
    }
}
