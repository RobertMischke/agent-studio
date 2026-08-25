namespace AgentStudio.Cli;

/// <summary>
/// Detects installed Claude/Codex CLI version drift at startup and on a
/// bounded periodic cadence. The persisted quota cache supplies the previous
/// startup observation, while the in-memory map handles changes during one
/// backend lifetime.
/// </summary>
public sealed class CliVersionMonitor : BackgroundService
{
    private readonly CliRouter _router;
    private readonly QuotaCacheStore _quotaCache;
    private readonly ILogger<CliVersionMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, string> _versions = new(StringComparer.OrdinalIgnoreCase);

    public CliVersionMonitor(
        CliRouter router,
        QuotaCacheStore quotaCache,
        IConfiguration configuration,
        ILogger<CliVersionMonitor> logger)
    {
        _router = router;
        _quotaCache = quotaCache;
        _logger = logger;
        var minutes = configuration.GetValue("CliVersionMonitor:IntervalMinutes", 15);
        _interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var snapshot in _quotaCache.Read())
        {
            if (!string.IsNullOrWhiteSpace(snapshot.CliType)
                && !string.IsNullOrWhiteSpace(snapshot.CliVersion))
            {
                _versions[snapshot.CliType] = snapshot.CliVersion;
            }
        }

        await CheckAllAsync("startup", stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAllAsync("periodic", stoppingToken);
        }
    }

    private async Task CheckAllAsync(string phase, CancellationToken ct)
    {
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            try
            {
                var cli = _router.Get(cliType);
                var probe = await Task.Run(() => cli.TestCliPath(), ct);
                var previous = _versions.GetValueOrDefault(cliType);
                var decision = CliVersionChangePolicy.Evaluate(previous, probe.Available ? probe.Version : null);

                if (!decision.Available)
                {
                    _logger.LogWarning(
                        "cli_version_check_failed cli={CliType} phase={Phase} path={Path}",
                        cliType, phase, probe.Path);
                    continue;
                }

                _versions[cliType] = decision.CurrentVersion!;
                if (decision.Changed)
                {
                    _logger.LogWarning(
                        "CLI version changed cli={CliType} previousVersion={PreviousVersion} currentVersion={CurrentVersion} phase={Phase}",
                        cliType, decision.PreviousVersion, decision.CurrentVersion, phase);
                }
                else if (decision.PreviousVersion == null || phase == "startup")
                {
                    _logger.LogInformation(
                        "cli_version_observed cli={CliType} version={Version} phase={Phase}",
                        cliType, decision.CurrentVersion, phase);
                }
                else
                {
                    _logger.LogDebug(
                        "cli_version_unchanged cli={CliType} version={Version} phase={Phase}",
                        cliType, decision.CurrentVersion, phase);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cli_version_check_failed cli={CliType} phase={Phase}", cliType, phase);
            }
        }
    }
}

public static class CliVersionChangePolicy
{
    public static CliVersionChangeDecision Evaluate(string? previousVersion, string? currentVersion)
    {
        var previous = string.IsNullOrWhiteSpace(previousVersion) ? null : previousVersion.Trim();
        var current = string.IsNullOrWhiteSpace(currentVersion) ? null : currentVersion.Trim();
        return new CliVersionChangeDecision(
            Available: current != null,
            Changed: previous != null && current != null
                && !string.Equals(previous, current, StringComparison.Ordinal),
            PreviousVersion: previous,
            CurrentVersion: current);
    }
}

public sealed record CliVersionChangeDecision(
    bool Available,
    bool Changed,
    string? PreviousVersion,
    string? CurrentVersion);
