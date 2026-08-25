namespace AgentStudio.Cli;

/// <summary>
/// Attributes CLI output drift to the executable that is actually installed.
/// Versions are sampled at startup and periodically; only startup and changes
/// are logged so a long-running backend stays quiet when nothing changed.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    public CliVersionMonitorHostedService(
        CliRouter router,
        IConfiguration configuration,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        _router = router;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CheckVersions();
        using var timer = new PeriodicTimer(ResolveInterval());
        while (await timer.WaitForNextTickAsync(stoppingToken)) CheckVersions();
    }

    internal void CheckVersions()
    {
        foreach (var cli in _router.All)
        {
            var (available, version, path) = cli.TestCliPath();
            var current = available && !string.IsNullOrWhiteSpace(version)
                ? version
                : "<unavailable>";
            if (!_versions.TryGetValue(cli.CliType, out var previous))
            {
                _versions[cli.CliType] = current;
                _logger.LogInformation(
                    "CLI version changed cli={Cli} previous={Previous} current={Current} path={Path}",
                    cli.CliType, "<startup>", current, path);
            }
            else if (!string.Equals(previous, current, StringComparison.Ordinal))
            {
                _versions[cli.CliType] = current;
                _logger.LogInformation(
                    "CLI version changed cli={Cli} previous={Previous} current={Current} path={Path}",
                    cli.CliType, previous, current, path);
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = int.TryParse(_configuration["CliVersionMonitor:IntervalMinutes"], out var configured)
            ? Math.Clamp(configured, 1, 1440)
            : 15;
        return TimeSpan.FromMinutes(minutes);
    }
}
