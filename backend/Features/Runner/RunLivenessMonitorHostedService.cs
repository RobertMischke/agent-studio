using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Uptime driver for the run-liveness invariant. The boot adoption scan
/// (<see cref="RunLivenessMonitor.AdoptOnBootAsync"/>) catches every zombie
/// present at startup; this service keeps the same idempotent monitor running
/// while the backend stays up so a card whose owning run dies DURING uptime
/// (e.g. a foreign backend sharing the workspace crashed) is demoted within the
/// 60s budget rather than lingering until the hour-scale
/// <see cref="StaleProgressSweepHostedService"/> window.
///
/// <para>
/// Cadence: <c>Runner:RunLiveness:IntervalSeconds</c> (default 15s), clamped to
/// [5s, 55s]. With the default uptime grace (30s) the worst-case demotion
/// latency after a run dies is grace + one interval, comfortably inside 60s.
/// </para>
/// </summary>
public sealed class RunLivenessMonitorHostedService : BackgroundService
{
    public const int DefaultIntervalSeconds = 15;

    private readonly RunLivenessMonitor _monitor;
    private readonly IConfiguration _config;
    private readonly ILogger<RunLivenessMonitorHostedService> _logger;

    public RunLivenessMonitorHostedService(
        RunLivenessMonitor monitor,
        IConfiguration config,
        ILogger<RunLivenessMonitorHostedService> logger)
    {
        _monitor = monitor;
        _config = config;
        _logger = logger;
    }

    public Task<IReadOnlyList<RunLivenessOutcome>> RunOnceAsync(CancellationToken ct = default)
        => _monitor.SweepAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Runner:RunLiveness:Enabled", true))
        {
            _logger.LogInformation("RunLivenessMonitorHostedService: disabled via Runner:RunLiveness:Enabled=false");
            return;
        }

        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

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

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RunLivenessMonitor uptime sweep failed");
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var seconds = _config.GetValue<int?>("Runner:RunLiveness:IntervalSeconds") ?? DefaultIntervalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 55));
    }
}
