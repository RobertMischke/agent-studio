namespace AgentStudio.Cli;

/// <summary>
/// Observes installed Claude and Codex versions at startup and periodically so
/// quota/parser drift can be tied to the exact CLI upgrade that preceded it.
/// The last quota-cache version seeds the first comparison across backend
/// restarts; later comparisons use the version observed in this process.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    private readonly CliRouter _router;
    private readonly QuotaCacheStore _quotaCache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _seeded;

    public CliVersionMonitorHostedService(
        CliRouter router,
        QuotaCacheStore quotaCache,
        IConfiguration configuration,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        _router = router;
        _quotaCache = quotaCache;
        _configuration = configuration;
        _logger = logger;
    }

    public void RunOnce()
    {
        SeedFromQuotaCache();
        foreach (var cli in _router.All.Where(IsTrackedCli))
        {
            var probe = cli.TestCliPath();
            var current = CliVersionIdentity.Normalize(probe.Version);
            _known.TryGetValue(cli.CliType, out var previous);
            switch (CliVersionIdentity.Classify(probe.Available, previous, current))
            {
                case CliVersionObservation.Unavailable:
                    _logger.LogDebug("CLI version check unavailable cli={Cli} path={Path}", cli.CliType, probe.Path);
                    continue;
                case CliVersionObservation.FirstSeen:
                    _logger.LogInformation("CLI version observed cli={Cli} version={Version}", cli.CliType, current);
                    break;
                case CliVersionObservation.Changed:
                    _logger.LogInformation(
                        "CLI version changed cli={Cli} previous={PreviousVersion} current={CurrentVersion}",
                        cli.CliType, previous, current);
                    break;
                case CliVersionObservation.Unchanged:
                    break;
            }

            _known[cli.CliType] = current!;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!(_configuration.GetValue<bool?>("CliVersionMonitor:Enabled") ?? true))
        {
            _logger.LogInformation("CLI version monitor disabled via CliVersionMonitor:Enabled=false");
            return;
        }

        await Task.Run(RunOnce, CancellationToken.None);
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

            await Task.Run(RunOnce, CancellationToken.None);
        }
    }

    private void SeedFromQuotaCache()
    {
        if (_seeded) return;
        _seeded = true;
        foreach (var snapshot in _quotaCache.Read())
        {
            if (IsTrackedCli(snapshot.CliType) && !string.IsNullOrWhiteSpace(snapshot.CliVersion))
                _known[snapshot.CliType] = snapshot.CliVersion;
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = _configuration.GetValue<int?>("CliVersionMonitor:IntervalMinutes") ?? 10;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }

    private static bool IsTrackedCli(ICliExecutionService cli) => IsTrackedCli(cli.CliType);

    private static bool IsTrackedCli(string? cliType)
        => string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
           || string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase);
}
