using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Local-v1 queue telemetry and transition alarm. Networked Studio proxies the
/// same route to the standalone Task Server, where authority and monitoring
/// live together.
/// </summary>
public sealed class AutoReviewQueueTelemetryWatchdog(
    AttemptAuthorityService authority,
    IConfiguration configuration,
    ILogger<AutoReviewQueueTelemetryWatchdog> logger) : BackgroundService
{
    private readonly object _gate = new();
    private Contract.AutoReviewQueueTelemetrySnapshot _current = Empty();
    private DateTime? _lastWarningAt;

    public Contract.AutoReviewQueueTelemetrySnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public Contract.AutoReviewQueueTelemetrySnapshot Refresh(DateTime? nowUtc = null)
    {
        var next = authority.GetAutoReviewQueueTelemetry(
            (nowUtc ?? DateTime.UtcNow).ToUniversalTime(),
            TimeSpan.FromMinutes(Value("RateWindowMinutes", 60, 1, 24 * 60)),
            TimeSpan.FromMinutes(Value("DurationWindowMinutes", 24 * 60, 1, 30 * 24 * 60)),
            TimeSpan.FromMinutes(Value("StagnantThresholdMinutes", 30, 1, 24 * 60)));
        lock (_gate)
        {
            Publish(_current, next);
            _current = next;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Value("IntervalSeconds", 30, 5, 15 * 60)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("auto-review-queue-telemetry-watchdog-stopped");
        }
    }

    private void RefreshSafely()
    {
        try
        {
            Refresh();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "auto-review-queue-telemetry-refresh-failed");
        }
    }

    internal void Publish(
        Contract.AutoReviewQueueTelemetrySnapshot previous,
        Contract.AutoReviewQueueTelemetrySnapshot next)
    {
        if (!next.IsStagnant)
        {
            if (previous.IsStagnant)
            {
                logger.LogInformation(
                    "auto-review-queue-stagnation-recovered queueDepth={QueueDepth} drainRatePerHour={DrainRatePerHour}",
                    next.QueueDepth,
                    next.DrainRatePerHour);
            }
            _lastWarningAt = null;
            return;
        }

        var repeatDue = _lastWarningAt is null
                        || next.ObservedAt - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (previous.IsStagnant && !repeatDue) return;

        logger.LogWarning(
            "auto-review-queue-stagnant queueDepth={QueueDepth} activeReviews={ActiveReviews} stagnantSince={StagnantSince} lastDrainAt={LastDrainAt} drainRatePerHour={DrainRatePerHour} medianReviewDurationSeconds={MedianReviewDurationSeconds}",
            next.QueueDepth,
            next.ActiveReviews,
            next.StagnantSince,
            next.LastDrainAt,
            next.DrainRatePerHour,
            next.MedianReviewDurationSeconds);
        _lastWarningAt = next.ObservedAt;
    }

    private int Value(string name, int fallback, int minimum, int maximum)
        => Math.Clamp(
            configuration.GetValue<int?>(
                $"AutoReviewQueueTelemetry:{name}") ?? fallback,
            minimum,
            maximum);

    private static Contract.AutoReviewQueueTelemetrySnapshot Empty()
        => new(
            0, 0, 0, 0, 0, null, 0, null, null, DateTime.UtcNow,
            60, 24 * 60, 30, false, null);
}
