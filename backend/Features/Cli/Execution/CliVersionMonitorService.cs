namespace AgentStudio.Cli;

/// <summary>
/// Attributes quota parser drift to an executable change. Versions are sampled
/// at startup and periodically without launching a TUI. A persisted quota
/// snapshot seeds the baseline so a version change across backend restarts is
/// still visible.
/// </summary>
public sealed class CliVersionMonitorService : BackgroundService
{
    private readonly CliRouter _router;
    private readonly QuotaService _quota;
    private readonly ILogger<CliVersionMonitorService> _logger;
    private readonly TimeSpan _interval;
    private readonly CliVersionChangeTracker _tracker = new();

    public CliVersionMonitorService(
        CliRouter router,
        QuotaService quota,
        IConfiguration configuration,
        ILogger<CliVersionMonitorService> logger)
    {
        _router = router;
        _quota = quota;
        _logger = logger;
        var configuredSeconds = int.TryParse(
            configuration["CliVersionMonitor:IntervalSeconds"], out var seconds)
            ? seconds
            : 600;
        _interval = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 30, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var snapshot in _quota.GetCached().Snapshots)
        {
            _tracker.Seed(snapshot.CliType, snapshot.CliVersion);
        }

        ObserveAll("startup");

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ObserveAll("periodic");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("CLI version monitor stopped with the host");
        }
    }

    private void ObserveAll(string phase)
    {
        foreach (var cli in _router.All)
        {
            try
            {
                var (available, version, path) = cli.TestCliPath();
                if (!available || string.IsNullOrWhiteSpace(version)) continue;

                var change = _tracker.Observe(cli.CliType, version);
                if (change != null)
                {
                    _logger.LogWarning(
                        "CLI version changed cli={Cli} previous={PreviousVersion} current={CurrentVersion} phase={Phase} path={Path}",
                        cli.CliType, change.PreviousVersion, change.CurrentVersion, phase, path);
                }
                else if (phase == "startup")
                {
                    _logger.LogInformation(
                        "cli_version_observed cli={Cli} version={Version} phase=startup path={Path}",
                        cli.CliType, version, path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI version observation failed for {Cli}", cli.CliType);
            }
        }
    }
}

public sealed record CliVersionChange(string PreviousVersion, string CurrentVersion);

public sealed class CliVersionChangeTracker
{
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(string cliType, string? version)
    {
        if (!string.IsNullOrWhiteSpace(cliType) && !string.IsNullOrWhiteSpace(version))
        {
            _versions.TryAdd(cliType, version);
        }
    }

    public CliVersionChange? Observe(string cliType, string version)
    {
        if (!_versions.TryGetValue(cliType, out var previous))
        {
            _versions[cliType] = version;
            return null;
        }

        if (string.Equals(previous, version, StringComparison.Ordinal)) return null;

        _versions[cliType] = version;
        return new CliVersionChange(previous, version);
    }
}
