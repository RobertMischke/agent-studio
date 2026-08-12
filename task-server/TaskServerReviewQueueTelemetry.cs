using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<ReviewQueueTelemetryDto> GetReviewQueueTelemetryAsync(
        TimeSpan drainWindow,
        TimeSpan durationWindow,
        TimeSpan stagnationThreshold,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var now = UtcNow;
        var queueDepth = 0;
        var waitingDepth = 0;
        var activeReviews = 0;
        DateTime? oldestWaitingAt = null;

        await using (var command = Command(connection, """
            WITH latest_attempt AS (
                SELECT a.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY a.task_id
                           ORDER BY a.attempt_number DESC, a.created_at DESC
                       ) AS row_number
                  FROM review_attempts a
            ), queue AS (
                SELECT t.id,
                       t.updated_at,
                       a.status,
                       a.expires_at,
                       a.created_at AS attempt_created_at,
                       CASE
                           WHEN a.id IS NULL THEN 1
                           WHEN a.status IN ('queued', 'process-unknown') THEN 1
                           WHEN a.status = 'leased' AND a.expires_at <= $now THEN 1
                           ELSE 0
                       END AS is_waiting,
                       CASE
                           WHEN a.status = 'leased' AND a.expires_at > $now THEN 1
                           ELSE 0
                       END AS is_active
                  FROM tasks t
                  LEFT JOIN latest_attempt a
                    ON a.task_id = t.id AND a.row_number = 1
                 WHERE t.state = '4-auto-review'
            )
            SELECT COUNT(*),
                   COALESCE(SUM(is_waiting), 0),
                   COALESCE(SUM(is_active), 0),
                   MIN(CASE WHEN is_waiting = 1
                            THEN COALESCE(attempt_created_at, updated_at)
                       END)
              FROM queue;
            """, ("$now", Iso(now))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                queueDepth = Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture);
                waitingDepth = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
                activeReviews = Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture);
                oldestWaitingAt = reader.IsDBNull(3) ? null : Parse(reader.GetString(3));
            }
        }

        DateTime? lastDrainAt = null;
        await using (var command = Command(connection, """
            SELECT MAX(reported_at)
              FROM review_attempts
             WHERE reported_at IS NOT NULL
               AND outcome IS NOT NULL
               AND outcome <> 'ReviewInfra';
            """))
        {
            var value = Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(value)) lastDrainAt = Parse(value);
        }

        var sampleBoundary = now - (durationWindow > drainWindow ? durationWindow : drainWindow);
        var completions = new List<ReviewCompletionSampleDto>();
        await using (var command = Command(connection, """
            SELECT acquired_at, reported_at
              FROM review_attempts
             WHERE acquired_at IS NOT NULL
               AND reported_at IS NOT NULL
               AND outcome IS NOT NULL
               AND outcome <> 'ReviewInfra'
               AND reported_at >= $boundary
             ORDER BY reported_at;
            """, ("$boundary", Iso(sampleBoundary))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var acquiredAt = Parse(reader.GetString(0));
                var completedAt = Parse(reader.GetString(1));
                if (completedAt < acquiredAt) continue;
                completions.Add(new ReviewCompletionSampleDto(
                    completedAt,
                    (completedAt - acquiredAt).TotalSeconds));
            }
        }

        return ReviewQueueTelemetryPolicy.Evaluate(
            now,
            queueDepth,
            waitingDepth,
            activeReviews,
            oldestWaitingAt,
            lastDrainAt,
            completions,
            drainWindow,
            durationWindow,
            stagnationThreshold);
    }
}

/// <summary>
/// Polls durable ReviewAttempt authority so a stuck queue is visible even when
/// no operator has the Execution Hosts page open.
/// </summary>
public sealed class ReviewQueueTelemetryWatchdog : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;
    public const int DefaultDrainWindowMinutes = 60;
    public const int DefaultDurationWindowHours = 24;
    public const int DefaultStagnationThresholdMinutes = 30;

    private readonly TaskServerStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewQueueTelemetryWatchdog> _logger;
    private readonly object _gate = new();
    private ReviewQueueTelemetryDto _current = Empty();
    private bool _wasStagnant;
    private DateTime? _lastWarningAt;

    public ReviewQueueTelemetryWatchdog(
        TaskServerStore store,
        IConfiguration configuration,
        ILogger<ReviewQueueTelemetryWatchdog> logger)
    {
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    public ReviewQueueTelemetryDto Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task<ReviewQueueTelemetryDto> RefreshAsync(CancellationToken ct = default)
    {
        var next = await _store.GetReviewQueueTelemetryAsync(
            TimeSpan.FromMinutes(DrainWindowMinutes()),
            TimeSpan.FromHours(DurationWindowHours()),
            TimeSpan.FromMinutes(StagnationThresholdMinutes()),
            ct);
        lock (_gate)
        {
            PublishTransition(next);
            _current = next;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds()));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshSafelyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("auto-review-queue-watchdog-stopped");
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "auto-review-queue-watchdog-failed");
        }
    }

    private void PublishTransition(ReviewQueueTelemetryDto next)
    {
        if (!next.Stagnant)
        {
            if (_wasStagnant)
                _logger.LogInformation(
                    "auto-review-queue-stagnation-recovered queueDepth={QueueDepth} drainRatePerHour={DrainRatePerHour}",
                    next.QueueDepth,
                    next.DrainRatePerHour);
            _wasStagnant = false;
            _lastWarningAt = null;
            return;
        }

        var repeatDue = _lastWarningAt is null
                        || next.ObservedAt - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (_wasStagnant && !repeatDue) return;

        _logger.LogWarning(
            "auto-review-queue-stagnant queueDepth={QueueDepth} waitingDepth={WaitingDepth} activeReviews={ActiveReviews} stagnantForMinutes={StagnantForMinutes} lastDrainAt={LastDrainAt} oldestWaitingAt={OldestWaitingAt}",
            next.QueueDepth,
            next.WaitingDepth,
            next.ActiveReviews,
            next.StagnantForMinutes,
            next.LastDrainAt,
            next.OldestWaitingAt);
        _wasStagnant = true;
        _lastWarningAt = next.ObservedAt;
    }

    private int IntervalSeconds() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:IntervalSeconds")
        ?? DefaultIntervalSeconds,
        5,
        15 * 60);

    private int DrainWindowMinutes() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:DrainWindowMinutes")
        ?? DefaultDrainWindowMinutes,
        5,
        24 * 60);

    private int DurationWindowHours() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:DurationWindowHours")
        ?? DefaultDurationWindowHours,
        1,
        14 * 24);

    private int StagnationThresholdMinutes() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:StagnationThresholdMinutes")
        ?? DefaultStagnationThresholdMinutes,
        5,
        24 * 60);

    private static ReviewQueueTelemetryDto Empty() => new(
        DateTime.UtcNow,
        0,
        0,
        0,
        0,
        DefaultDrainWindowMinutes,
        null,
        DefaultDurationWindowHours,
        0,
        null,
        null,
        false,
        DefaultStagnationThresholdMinutes,
        0);
}
