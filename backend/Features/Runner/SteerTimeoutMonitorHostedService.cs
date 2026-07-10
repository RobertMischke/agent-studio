using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Uptime driver for the steer-timeout invariant (Run-Liveness Slice B, concept
/// Rule 2). Runs <see cref="SteerTimeoutMonitor.SweepAsync"/> on a short cadence
/// so an unanswered steer / NeedsInput wait is resolved (auto-answered or
/// escalated) within timeout + one interval, rather than hanging for hours as it
/// did on 2026-07-10 (belegt 2062/2067/2068).
///
/// <para>
/// Cadence: <c>Runner:SteerTimeout:IntervalSeconds</c> (default 20s), clamped to
/// [5s, 55s]. With the default 120s timeout the worst-case latency from timeout
/// to resolution is one interval.
/// </para>
/// </summary>
public sealed class SteerTimeoutMonitorHostedService : BackgroundService
{
    public const int DefaultIntervalSeconds = 20;

    private readonly SteerTimeoutMonitor _monitor;
    private readonly IConfiguration _config;
    private readonly ILogger<SteerTimeoutMonitorHostedService> _logger;

    public SteerTimeoutMonitorHostedService(
        SteerTimeoutMonitor monitor,
        IConfiguration config,
        ILogger<SteerTimeoutMonitorHostedService> logger)
    {
        _monitor = monitor;
        _config = config;
        _logger = logger;
    }

    public Task<IReadOnlyList<SteerTimeoutOutcome>> RunOnceAsync(CancellationToken ct = default)
        => _monitor.SweepAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Runner:SteerTimeout:Enabled", true))
        {
            _logger.LogInformation("SteerTimeoutMonitorHostedService: disabled via Runner:SteerTimeout:Enabled=false");
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
                _logger.LogWarning(ex, "SteerTimeoutMonitor uptime sweep failed");
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var seconds = _config.GetValue<int?>("Runner:SteerTimeout:IntervalSeconds") ?? DefaultIntervalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 55));
    }
}
