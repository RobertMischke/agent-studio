namespace AgentStudio.Cli;

/// <summary>
/// Lightweight startup and periodic CLI version observation. Quota snapshots
/// carry the version that produced them; this monitor adds an explicit drift
/// log before a changed TUI can turn into an unattributable parser failure.
/// </summary>
public sealed class CliVersionMonitorService : BackgroundService
{
    private static readonly string[] MonitoredCliTypes = [CliTypes.Claude, CliTypes.Codex];
    private readonly ILogger<CliVersionMonitorService> _logger;
    private readonly CliRouter _router;
    private readonly QuotaService _quota;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, string> _observed = new(StringComparer.OrdinalIgnoreCase);

    public CliVersionMonitorService(
        ILogger<CliVersionMonitorService> logger,
        CliRouter router,
        QuotaService quota,
        IConfiguration configuration)
    {
        _logger = logger;
        _router = router;
        _quota = quota;
        var minutes = int.TryParse(configuration["Quota:CliVersionCheckMinutes"], out var configured)
            ? configured
            : 15;
        _interval = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CheckVersions(startup: true);
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken)) CheckVersions(startup: false);
    }

    private void CheckVersions(bool startup)
    {
        foreach (var cliType in MonitoredCliTypes)
        {
            try
            {
                var (available, version, _) = _router.Get(cliType).TestCliPath();
                if (!available || string.IsNullOrWhiteSpace(version)) continue;

                var previous = startup
                    ? _quota.GetCachedFor(cliType)?.CliVersion
                    : (_observed.TryGetValue(cliType, out var observed) ? observed : null);
                _observed[cliType] = version;

                if (!string.IsNullOrWhiteSpace(previous)
                    && !string.Equals(previous, version, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "CLI version changed cli={CliType} previous={PreviousVersion} current={CurrentVersion}",
                        cliType, previous, version);
                }
                else if (startup)
                {
                    _logger.LogInformation(
                        "CLI version observed at startup cli={CliType} version={CliVersion}",
                        cliType, version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI version check failed for {CliType}", cliType);
            }
        }
    }
}
