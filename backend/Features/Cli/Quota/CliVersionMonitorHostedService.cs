namespace AgentStudio.Cli;

/// <summary>
/// Records the installed Claude and Codex versions at startup and periodically
/// thereafter. A change is attributable even across backend restarts because
/// the startup comparison uses the version persisted with the quota cache.
/// Version checks execute only each CLI's bounded <c>--version</c> probe; they do
/// not open the interactive TUI or consume quota.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly CliRouter _router;
    private readonly QuotaService _quota;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly CliVersionChangeTracker _tracker = new();

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

    internal void RunOnce()
    {
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            try
            {
                var (available, version, path) = _router.Get(cliType).TestCliPath();
                if (!available || string.IsNullOrWhiteSpace(version))
                {
                    _logger.LogWarning(
                        "CLI version check unavailable cli={Cli} path={Path}",
                        cliType,
                        path);
                    continue;
                }

                var cachedVersion = _quota.GetCachedFor(cliType)?.CliVersion;
                var observation = _tracker.Observe(cliType, version, cachedVersion);
                if (observation.Changed)
                {
                    _logger.LogInformation(
                        "CLI version changed cli={Cli} previous={PreviousVersion} current={CurrentVersion} path={Path}",
                        cliType,
                        observation.PreviousVersion,
                        observation.CurrentVersion,
                        path);
                }
                else if (observation.FirstObservation)
                {
                    _logger.LogInformation(
                        "CLI version observed at startup cli={Cli} version={Version} path={Path}",
                        cliType,
                        observation.CurrentVersion,
                        path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CLI version check failed cli={Cli}", cliType);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunOnce();
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

            RunOnce();
        }
    }

    private TimeSpan ResolveInterval()
    {
        var seconds = _configuration.GetValue<int?>("Quota:CliVersionCheckIntervalSeconds")
            ?? (int)DefaultInterval.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 24 * 60 * 60));
    }
}

internal sealed class CliVersionChangeTracker
{
    private readonly Dictionary<string, string> _lastObserved = new(StringComparer.OrdinalIgnoreCase);

    public CliVersionObservation Observe(string cliType, string currentVersion, string? persistedVersion = null)
    {
        var first = !_lastObserved.TryGetValue(cliType, out var priorInProcess);
        var previous = first ? persistedVersion : priorInProcess;
        _lastObserved[cliType] = currentVersion;
        return new CliVersionObservation(
            FirstObservation: first,
            Changed: !string.IsNullOrWhiteSpace(previous)
                && !string.Equals(previous, currentVersion, StringComparison.OrdinalIgnoreCase),
            PreviousVersion: previous,
            CurrentVersion: currentVersion);
    }
}

internal sealed record CliVersionObservation(
    bool FirstObservation,
    bool Changed,
    string? PreviousVersion,
    string CurrentVersion);
