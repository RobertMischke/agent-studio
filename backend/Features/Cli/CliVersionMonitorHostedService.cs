namespace AgentStudio.Cli;

/// <summary>
/// Records installed CLI versions at startup and on a bounded periodic tick.
/// A changed version is a first-class drift signal for quota parsers and other
/// CLI adapters, rather than a fact an operator must reconstruct later.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 15;

    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly Dictionary<string, CliVersionObservation> _observed =
        new(StringComparer.OrdinalIgnoreCase);

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
        ObserveAll();
        using var timer = new PeriodicTimer(ResolveInterval());
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            ObserveAll();
        }
    }

    internal void ObserveAll()
    {
        foreach (var cli in _router.All.OrderBy(c => c.CliType, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var (available, version, path) = cli.TestCliPath();
                var current = new CliVersionObservation(available, version, path);
                if (!_observed.TryGetValue(cli.CliType, out var previous))
                {
                    _logger.LogInformation(
                        "CLI version observed cli={Cli} version={Version} available={Available} path={Path}",
                        cli.CliType,
                        current.DisplayVersion,
                        available,
                        path);
                }
                else if (CliVersionChangePolicy.Changed(previous, current))
                {
                    // Stable phrase intentionally shared with AGT-2673 diagnostics.
                    _logger.LogWarning(
                        "CLI version changed cli={Cli} previous={Previous} current={Current} available={Available} path={Path}",
                        cli.CliType,
                        previous.DisplayVersion,
                        current.DisplayVersion,
                        available,
                        path);
                }

                _observed[cli.CliType] = current;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CLI version check failed cli={Cli}", cli.CliType);
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = Math.Clamp(
            _configuration.GetValue<int?>("CliVersionMonitor:IntervalMinutes")
                ?? DefaultIntervalMinutes,
            1,
            24 * 60);
        return TimeSpan.FromMinutes(minutes);
    }
}

public sealed record CliVersionObservation(bool Available, string? Version, string Path)
{
    public string DisplayVersion => Available && !string.IsNullOrWhiteSpace(Version)
        ? Version
        : "<unavailable>";
}

public static class CliVersionChangePolicy
{
    public static bool Changed(CliVersionObservation previous, CliVersionObservation current)
        => previous.Available != current.Available
           || !string.Equals(previous.Version, current.Version, StringComparison.Ordinal)
           || !string.Equals(previous.Path, current.Path, StringComparison.OrdinalIgnoreCase);
}
