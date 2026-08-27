namespace AgentStudio.Cli;

/// <summary>
/// Attributes quota parser drift to executable upgrades by checking the CLI
/// versions at startup and on a bounded periodic cadence. The first check is
/// compared with the version persisted in the last quota snapshot, so an
/// upgrade that happened while the backend was stopped is still visible.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 30;

    private static readonly string[] MonitoredCliTypes = [CliTypes.Claude, CliTypes.Codex];
    private readonly CliRouter _router;
    private readonly QuotaService _quota;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly Dictionary<string, string> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public CliVersionMonitorHostedService(
        CliRouter router,
        QuotaService quota,
        IConfiguration configuration,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        _router = router;
        _quota = quota;
        _configuration = configuration;
        _logger = logger;
    }

    internal void CheckOnce(bool startup)
    {
        foreach (var cliType in MonitoredCliTypes)
        {
            var (available, version, path) = _router.Get(cliType).TestCliPath();
            if (!available || string.IsNullOrWhiteSpace(version))
            {
                _logger.LogDebug(
                    "CLI version check unavailable cli={Cli} path={Path} startup={Startup}",
                    cliType,
                    path,
                    startup);
                continue;
            }

            var previous = _lastSeen.TryGetValue(cliType, out var seen)
                ? seen
                : _quota.GetCachedFor(cliType)?.CliVersion;

            if (!string.IsNullOrWhiteSpace(previous)
                && !string.Equals(previous, version, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "CLI version changed cli={Cli} previous={PreviousVersion} current={CurrentVersion} path={Path} startup={Startup}",
                    cliType,
                    previous,
                    version,
                    path,
                    startup);
            }
            else if (startup)
            {
                _logger.LogInformation(
                    "CLI version check cli={Cli} version={Version} path={Path} startup=true",
                    cliType,
                    version,
                    path);
            }

            _lastSeen[cliType] = version;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        CheckSafely(startup: true);

        using var timer = new PeriodicTimer(ResolveInterval());
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

            CheckSafely(startup: false);
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = Math.Clamp(
            _configuration.GetValue<int?>("Quota:CliVersionCheckMinutes")
                ?? DefaultIntervalMinutes,
            5,
            24 * 60);
        return TimeSpan.FromMinutes(minutes);
    }

    private void CheckSafely(bool startup)
    {
        try
        {
            CheckOnce(startup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLI version check failed startup={Startup}", startup);
        }
    }
}
