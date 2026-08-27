using System.Collections.Concurrent;

namespace AgentStudio.Cli;

/// <summary>
/// Tracks CLI versions observed by startup checks and quota probes. The first
/// value is a baseline; subsequent changes emit one attributable drift event.
/// </summary>
public sealed class CliVersionTracker
{
    private readonly ILogger<CliVersionTracker> _logger;
    private readonly ConcurrentDictionary<string, string> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    public CliVersionTracker(ILogger<CliVersionTracker> logger) => _logger = logger;

    public string? Current(string cliType)
        => _versions.TryGetValue(cliType, out var version) ? version : null;

    public void Seed(string cliType, string? version)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(version)) return;
        _versions.TryAdd(cliType, version.Trim());
    }

    public void Observe(string cliType, string? version, string source)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(version)) return;
        var current = version.Trim();

        while (true)
        {
            if (!_versions.TryGetValue(cliType, out var previous))
            {
                if (_versions.TryAdd(cliType, current))
                {
                    _logger.LogInformation(
                        "CLI version baseline cli={Cli} version={Version} source={Source}",
                        cliType, current, source);
                    return;
                }
                continue;
            }

            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;
            if (!_versions.TryUpdate(cliType, current, previous)) continue;

            _logger.LogWarning(
                "CLI version changed cli={Cli} previous={PreviousVersion} current={CurrentVersion} source={Source}",
                cliType, previous, current, source);
            return;
        }
    }
}
