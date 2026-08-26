using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Records the installed Claude and Codex versions at startup and periodically
/// thereafter. A persisted baseline makes an upgrade attributable even when it
/// happened while the backend was stopped.
/// </summary>
public sealed class CliVersionMonitorHostedService : BackgroundService
{
    private static readonly string[] MonitoredCliTypes = [CliTypes.Claude, CliTypes.Codex];
    private readonly ILogger<CliVersionMonitorHostedService> _logger;
    private readonly CliRouter _router;
    private readonly TimeSpan _interval;
    private readonly string _statePath;
    private readonly Dictionary<string, string> _lastSeen;

    public CliVersionMonitorHostedService(
        ILogger<CliVersionMonitorHostedService> logger,
        CliRouter router,
        IConfiguration configuration)
    {
        _logger = logger;
        _router = router;
        _interval = TimeSpan.FromMinutes(
            int.TryParse(configuration["CliVersionMonitor:IntervalMinutes"], out var minutes)
                ? Math.Clamp(minutes, 1, 1440)
                : 5);

        var taskRepository = configuration["TaskRepository"];
        var runtimeDirectory = !string.IsNullOrWhiteSpace(taskRepository)
            ? Path.Combine(taskRepository, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        _statePath = Path.Combine(runtimeDirectory, "cli-versions.json");
        _lastSeen = ReadState(_statePath, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        CheckVersions("startup");

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                CheckVersions("periodic");
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "CLI version monitor stopped with the host");
        }
    }

    private void CheckVersions(string check)
    {
        var changed = false;
        foreach (var cliType in MonitoredCliTypes)
        {
            try
            {
                var (available, version, path) = _router.Get(cliType).TestCliPath();
                if (!available || string.IsNullOrWhiteSpace(version))
                {
                    _logger.LogDebug(
                        "cli_version_check cli={Cli} available={Available} path={Path} check={Check}",
                        cliType,
                        available,
                        path,
                        check);
                    continue;
                }

                version = version.Trim();
                _lastSeen.TryGetValue(cliType, out var previous);
                if (HasChanged(previous, version))
                {
                    _logger.LogWarning(
                        "CLI version changed cli={Cli} previous={Previous} current={Current} path={Path} check={Check}",
                        cliType,
                        previous,
                        version,
                        path,
                        check);
                }
                else if (previous is null)
                {
                    _logger.LogInformation(
                        "cli_version_observed cli={Cli} version={Version} path={Path} check={Check}",
                        cliType,
                        version,
                        path,
                        check);
                }

                if (!string.Equals(previous, version, StringComparison.Ordinal))
                {
                    _lastSeen[cliType] = version;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI version check failed for {Cli} during {Check}", cliType, check);
            }
        }

        if (changed) WriteState(_statePath, _lastSeen, _logger);
    }

    internal static bool HasChanged(string? previous, string current)
        => !string.IsNullOrWhiteSpace(previous)
           && !string.Equals(previous.Trim(), current.Trim(), StringComparison.Ordinal);

    private static Dictionary<string, string> ReadState(
        string path,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read CLI version state at {Path}", path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteState(
        string path,
        IReadOnlyDictionary<string, string> values,
        ILogger<CliVersionMonitorHostedService> logger)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist CLI version state at {Path}", path);
        }
    }
}
