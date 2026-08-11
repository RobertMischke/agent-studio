using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

/// <summary>
/// Emits transition-based operator alarms when claimable Review Plane work has
/// produced no accepted draining report for the configured threshold.
/// </summary>
public sealed class AutoReviewQueueTelemetryMonitor(
    TaskServerStore store,
    IOptions<TaskServerOptions> options,
    ILogger<AutoReviewQueueTelemetryMonitor> logger) : BackgroundService
{
    private bool _wasStagnant;
    private DateTime? _lastWarningAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafely(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            options.Value.AutoReviewQueueTelemetryIntervalSeconds,
            5,
            15 * 60));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshSafely(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("auto-review-queue-telemetry-monitor-stopped");
        }
    }

    private async Task RefreshSafely(CancellationToken ct)
    {
        try
        {
            Publish(await store.GetAutoReviewQueueTelemetryAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "auto-review-queue-telemetry-refresh-failed");
        }
    }

    internal void Publish(AutoReviewQueueTelemetrySnapshot snapshot)
    {
        if (!snapshot.IsStagnant)
        {
            if (_wasStagnant)
            {
                logger.LogInformation(
                    "auto-review-queue-stagnation-recovered queueDepth={QueueDepth} drainRatePerHour={DrainRatePerHour}",
                    snapshot.QueueDepth,
                    snapshot.DrainRatePerHour);
            }
            _wasStagnant = false;
            _lastWarningAt = null;
            return;
        }

        var repeatDue = _lastWarningAt is null
                        || snapshot.ObservedAt - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (_wasStagnant && !repeatDue) return;

        logger.LogWarning(
            "auto-review-queue-stagnant queueDepth={QueueDepth} activeReviews={ActiveReviews} stagnantSince={StagnantSince} lastDrainAt={LastDrainAt} drainRatePerHour={DrainRatePerHour} medianReviewDurationSeconds={MedianReviewDurationSeconds}",
            snapshot.QueueDepth,
            snapshot.ActiveReviews,
            snapshot.StagnantSince,
            snapshot.LastDrainAt,
            snapshot.DrainRatePerHour,
            snapshot.MedianReviewDurationSeconds);
        _wasStagnant = true;
        _lastWarningAt = snapshot.ObservedAt;
    }
}
