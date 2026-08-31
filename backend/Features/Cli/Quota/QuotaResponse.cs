namespace AgentStudio.Cli;

/// <summary>
/// HTTP projection for a cached quota reading. The internal snapshot keeps its
/// historical <c>FetchedAt</c> name; the boundary exposes the operator-facing
/// <c>CapturedAt</c> contract plus explicit freshness state.
/// </summary>
public sealed record QuotaSnapshotResponse
{
    public string CliType { get; init; } = "";
    public DateTime? CapturedAt { get; init; }
    public DateTime FetchedAt { get; init; }
    public long? AgeSeconds { get; init; }
    public bool Stale { get; init; }
    public bool ProbeFailed { get; init; }
    public DateTime? ProbeFailedAt { get; init; }
    public string? CliVersion { get; init; }
    public string? Plan { get; init; }
    public List<QuotaWindow> Windows { get; init; } = [];
    public string? Source { get; init; }
    public string? RawSample { get; init; }
    public string? Error { get; init; }
    public bool Suspicious { get; init; }
    public string? SuspiciousReason { get; init; }
}

public sealed record QuotaReportResponse
{
    public DateTime At { get; init; }
    public int TtlSeconds { get; init; }
    public List<QuotaSnapshotResponse> Snapshots { get; init; } = [];

    public static QuotaReportResponse From(QuotaReport report, DateTime now)
    {
        var ttl = TimeSpan.FromSeconds(report.TtlSeconds);
        return new QuotaReportResponse
        {
            At = now,
            TtlSeconds = report.TtlSeconds,
            Snapshots = report.Snapshots
                .Select(snapshot => FromSnapshot(snapshot, ttl, now))
                .ToList()
        };
    }

    public static QuotaSnapshotResponse FromSnapshot(
        QuotaSnapshot snapshot,
        TimeSpan ttl,
        DateTime now)
    {
        var hasReading = snapshot.Windows.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.Plan);
        var age = hasReading ? now - snapshot.FetchedAt : (TimeSpan?)null;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var probeFailed = snapshot.ProbeFailedAt.HasValue || !string.IsNullOrWhiteSpace(snapshot.Error);
        return new QuotaSnapshotResponse
        {
            CliType = snapshot.CliType,
            CapturedAt = hasReading ? snapshot.FetchedAt : null,
            FetchedAt = snapshot.FetchedAt,
            AgeSeconds = age.HasValue ? Math.Max(0, (long)age.Value.TotalSeconds) : null,
            Stale = !hasReading || probeFailed || age > ttl,
            ProbeFailed = probeFailed,
            ProbeFailedAt = snapshot.ProbeFailedAt,
            CliVersion = snapshot.CliVersion,
            Plan = snapshot.Plan,
            Windows = snapshot.Windows,
            Source = snapshot.Source,
            RawSample = snapshot.RawSample,
            Error = QuotaService.NormalizeProbeError(snapshot.Error),
            Suspicious = snapshot.Suspicious,
            SuspiciousReason = snapshot.SuspiciousReason
        };
    }
}
