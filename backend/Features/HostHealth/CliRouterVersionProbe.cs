using AgentStudio.Cli;

namespace AgentStudio.HostHealth;

/// <summary>
/// Adapter onto the existing local capability probe. The CLI layer already
/// knows how to resolve a binary (including Windows PATHEXT and npm shims) and
/// ask it for its version; host health only needs the verdict, so this is the
/// one place the two features touch.
/// </summary>
public sealed class CliRouterVersionProbe : ILocalCliVersionProbe
{
    private readonly CliRouter _router;
    private readonly ILogger<CliRouterVersionProbe> _logger;

    public CliRouterVersionProbe(CliRouter router, ILogger<CliRouterVersionProbe> logger)
    {
        _router = router;
        _logger = logger;
    }

    public (bool Available, string? Version) Probe(string cliType)
    {
        try
        {
            var (available, version, _) = _router.Get(cliType).TestCliPath();
            return (available, version);
        }
        catch (Exception ex)
        {
            // An unroutable CLI type or a probe that throws is the same signal
            // as a failed probe here: not available, no version.
            _logger.LogDebug(ex, "Version probe for '{CliType}' threw; treating the CLI as unavailable", cliType);
            return (false, null);
        }
    }
}
