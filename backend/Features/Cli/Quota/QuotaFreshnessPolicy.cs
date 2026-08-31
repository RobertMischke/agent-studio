namespace AgentStudio.Cli;

/// <summary>
/// Pure read-time policy for the last-good quota freshness fields exposed by
/// the API. Probe failures make a reading stale immediately; otherwise the
/// configured TTL determines the boundary.
/// </summary>
public static class QuotaFreshnessPolicy
{
    public static QuotaFreshness Evaluate(QuotaSnapshot snapshot, TimeSpan ttl, DateTime now)
    {
        var capturedAt = snapshot.CapturedAt ?? snapshot.FetchedAt;
        var age = now > capturedAt ? now - capturedAt : TimeSpan.Zero;
        var hasReading = snapshot.Windows.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.Plan);
        var isStale = !hasReading || snapshot.ProbeFailedAt != null || age > ttl;
        var staleSince = snapshot.ProbeFailedAt
            ?? (hasReading && age > ttl ? capturedAt + ttl : null);

        return new QuotaFreshness(
            CapturedAt: capturedAt,
            IsStale: isStale,
            AgeSeconds: Math.Max(0, (long)age.TotalSeconds),
            StaleSince: staleSince);
    }
}

public sealed record QuotaFreshness(
    DateTime CapturedAt,
    bool IsStale,
    long AgeSeconds,
    DateTime? StaleSince);
