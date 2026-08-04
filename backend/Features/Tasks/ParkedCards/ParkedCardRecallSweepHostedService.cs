namespace AgentStudio.Tasks;

/// <summary>
/// Runs <see cref="ParkedCardRecallSweep"/> periodically. The cadence is
/// deliberately slow: a resolved blocker is a "look at this card again" signal
/// for a person, not a latency-sensitive event, and the AGT-2220 gap was four
/// days - anything under an hour already turns that standstill into same-day
/// visibility.
/// </summary>
public sealed class ParkedCardRecallSweepHostedService : BackgroundService
{
    public const int DefaultIntervalMinutes = 30;

    private readonly ParkedCardRecallSweep _sweep;
    private readonly IConfiguration _config;
    private readonly ILogger<ParkedCardRecallSweepHostedService> _logger;

    public ParkedCardRecallSweepHostedService(
        ParkedCardRecallSweep sweep,
        IConfiguration config,
        ILogger<ParkedCardRecallSweepHostedService> logger)
    {
        _sweep = sweep;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            try
            {
                _sweep.Sweep(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Parked-card recall sweep failed");
            }
        }
    }

    private TimeSpan ResolveInterval()
    {
        var minutes = _config.GetValue<int?>("Supervisor:ParkedCardRecallSweepIntervalMinutes")
            ?? DefaultIntervalMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }
}
