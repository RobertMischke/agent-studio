namespace AgentStudio.HostHealth;

/// <summary>
/// Turns local CLI capability probing into a loop instead of a boot-time
/// one-shot. Before this, "claude is not available" was only noticed when a
/// pickup tried to spawn it - which is how a control-plane host could lose its
/// CLI at minute twelve and only reveal it through drained pickups.
///
/// <para>
/// The loop runs once at startup and then on a fixed interval. It repairs at
/// most one CLI per tick per hour (<see cref="LocalCliRepairThrottle"/>), so
/// the steady-state cost on a healthy host is two <c>--version</c> probes and
/// a handful of file-existence checks.
/// </para>
/// </summary>
public sealed class LocalCliHealthHostedService : BackgroundService
{
    /// <summary>
    /// Long enough to stay invisible in the process list, short enough that an
    /// auto-update that breaks the install mid-session is caught before the
    /// operator notices through a drained lane.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>Let the CLI router and configuration settle before the first probe.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly LocalCliHealthService _health;
    private readonly ILogger<LocalCliHealthHostedService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public LocalCliHealthHostedService(
        LocalCliHealthService health,
        IConfiguration configuration,
        ILogger<LocalCliHealthHostedService> logger)
    {
        _health = health;
        _logger = logger;
        _enabled = configuration.GetValue("HostHealth:CliSelfHealEnabled", true);
        var minutes = configuration.GetValue<int?>("HostHealth:CliProbeIntervalMinutes");
        _interval = minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Local CLI self-heal is disabled (HostHealth:CliSelfHealEnabled=false).");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "LocalCliHealthHostedService: shutdown cancelled the probe loop");
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        foreach (var package in LocalCliPackage.Known)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await _health.EnsureHealthyAsync(package.CliType, operatorRequested: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One CLI's health check must not stop the other's.
                _logger.LogWarning(ex, "Local CLI health check for {CliType} failed", package.CliType);
            }
        }
    }
}
