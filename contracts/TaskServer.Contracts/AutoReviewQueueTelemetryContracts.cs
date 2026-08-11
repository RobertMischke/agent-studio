namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One authority-owned review attempt projected into queue telemetry. The
/// projection deliberately carries lifecycle facts instead of storage-specific
/// status strings so the monolith and standalone Task Server use one policy.
/// </summary>
public sealed record AutoReviewQueueAttemptFact(
    DateTime CreatedAt,
    DateTime? AcquiredAt,
    DateTime? ExpiresAt,
    DateTime? ReportedAt,
    bool Open,
    bool ProcessUnknown,
    bool CountsAsDrain,
    bool Superseded);

/// <summary>Current Review Plane queue health and rolling throughput.</summary>
public sealed record AutoReviewQueueTelemetrySnapshot(
    int QueueDepth,
    int ActiveReviews,
    int OutstandingReviews,
    int CompletedReviewsInRateWindow,
    double DrainRatePerHour,
    double? MedianReviewDurationSeconds,
    int ReviewDurationSampleCount,
    DateTime? OldestQueuedAt,
    DateTime? LastDrainAt,
    DateTime ObservedAt,
    int RateWindowMinutes,
    int DurationWindowMinutes,
    int StagnantThresholdMinutes,
    bool IsStagnant,
    DateTime? StagnantSince);

/// <summary>
/// Pure queue-health policy shared by both Review Plane authority stores.
/// Queue depth means claimable work, including an expired lease; active reviews
/// hold a live lease. Drain is an accepted Pass or ProductFailure report;
/// infrastructure retries and superseded reports do not advance the marker.
/// </summary>
public static class AutoReviewQueueTelemetryPolicy
{
    public static AutoReviewQueueTelemetrySnapshot Evaluate(
        DateTime nowUtc,
        TimeSpan rateWindow,
        TimeSpan durationWindow,
        TimeSpan stagnantThreshold,
        IEnumerable<AutoReviewQueueAttemptFact> attempts)
    {
        var now = nowUtc.ToUniversalTime();
        var rate = PositiveWindow(rateWindow, TimeSpan.FromHours(1));
        var duration = PositiveWindow(durationWindow, TimeSpan.FromHours(24));
        var threshold = PositiveWindow(stagnantThreshold, TimeSpan.FromMinutes(30));
        var facts = attempts.ToList();

        var active = facts
            .Where(fact => fact.Open)
            .Count(fact => !fact.ProcessUnknown
                           && fact.AcquiredAt is not null
                           && fact.ExpiresAt > now);
        var queued = facts
            .Where(fact => fact.Open)
            .Where(fact => fact.ProcessUnknown
                           || fact.AcquiredAt is null
                           || fact.ExpiresAt <= now)
            .OrderBy(fact => fact.CreatedAt)
            .ToList();
        var drained = facts
            .Where(fact => fact.CountsAsDrain
                           && !fact.Superseded
                           && fact.ReportedAt is not null)
            .ToList();
        var rateCutoff = now - rate;
        var completedInRateWindow = drained.Count(fact => fact.ReportedAt >= rateCutoff);
        var durationCutoff = now - duration;
        var durations = drained
            .Where(fact => fact.ReportedAt >= durationCutoff)
            .Where(fact => fact.AcquiredAt is not null && fact.ReportedAt >= fact.AcquiredAt)
            .Select(fact => (fact.ReportedAt!.Value - fact.AcquiredAt!.Value).TotalSeconds)
            .Order()
            .ToList();
        var oldestQueuedAt = queued.FirstOrDefault()?.CreatedAt;
        var lastDrainAt = drained
            .Select(fact => fact.ReportedAt)
            .Where(at => at is not null)
            .Max();
        var stagnantSince = oldestQueuedAt is null
            ? null
            : lastDrainAt is not null && lastDrainAt > oldestQueuedAt
                ? lastDrainAt
                : oldestQueuedAt;
        var isStagnant = queued.Count > 0
                         && stagnantSince is not null
                         && now - stagnantSince >= threshold;

        return new AutoReviewQueueTelemetrySnapshot(
            QueueDepth: queued.Count,
            ActiveReviews: active,
            OutstandingReviews: queued.Count + active,
            CompletedReviewsInRateWindow: completedInRateWindow,
            DrainRatePerHour: Math.Round(completedInRateWindow / rate.TotalHours, 2),
            MedianReviewDurationSeconds: Median(durations),
            ReviewDurationSampleCount: durations.Count,
            OldestQueuedAt: oldestQueuedAt,
            LastDrainAt: lastDrainAt,
            ObservedAt: now,
            RateWindowMinutes: Math.Max(1, (int)Math.Round(rate.TotalMinutes)),
            DurationWindowMinutes: Math.Max(1, (int)Math.Round(duration.TotalMinutes)),
            StagnantThresholdMinutes: Math.Max(1, (int)Math.Round(threshold.TotalMinutes)),
            IsStagnant: isStagnant,
            StagnantSince: isStagnant ? stagnantSince : null);
    }

    private static TimeSpan PositiveWindow(TimeSpan value, TimeSpan fallback)
        => value > TimeSpan.Zero ? value : fallback;

    private static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return null;
        var middle = sorted.Count / 2;
        return Math.Round(
            sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2,
            2);
    }
}
