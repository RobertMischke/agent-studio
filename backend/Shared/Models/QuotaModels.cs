namespace AgentStudio.Shared;

/// <summary>
/// One quota window for a CLI subscription (e.g. monthly premium requests,
/// 5-hour sliding window, or weekly limit). All numeric fields are nullable
/// because each CLI exposes a different subset.
/// </summary>
public record QuotaWindow
{
    /// <summary>"Premium requests" / "5-hour" / "Weekly" / etc.</summary>
    public string Label { get; init; } = "";
    /// <summary>Percentage used, 0..100+. May exceed 100 when over-quota.</summary>
    public double? UsedPct { get; init; }
    /// <summary>Absolute used count when known.</summary>
    public double? Used { get; init; }
    /// <summary>Absolute plan limit when known.</summary>
    public double? Limit { get; init; }
    /// <summary>"requests" / "tokens" / "%".</summary>
    public string? Unit { get; init; }
    /// <summary>UTC timestamp when this window resets, when computable.</summary>
    public DateTime? ResetAt { get; init; }
    /// <summary>Original human-readable reset string from the CLI ("3:40am (Europe/Berlin)" / "Mar 1").</summary>
    public string? ResetLabel { get; init; }
}

/// <summary>
/// A single CLI's quota state at a point in time. <see cref="Error"/> is set
/// when probing failed; consumers should still display <see cref="Plan"/> and
/// any partial windows that did parse.
/// </summary>
public record QuotaSnapshot
{
    public string CliType { get; init; } = "";
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    /// <summary>"Pro" / "Pro+" / "Plus" / "Free" — null when unknown.</summary>
    public string? Plan { get; init; }
    public List<QuotaWindow> Windows { get; init; } = [];
    /// <summary>How the data was sourced: "/usage" / "/status" / "footer" / "banner".</summary>
    public string? Source { get; init; }
    /// <summary>Truncated raw snapshot for debugging in the UI.</summary>
    public string? RawSample { get; init; }
    /// <summary>Set when probing failed; <see cref="Plan"/>/<see cref="Windows"/> may still hold partial data.</summary>
    public string? Error { get; init; }
}

public record QuotaReport
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    /// <summary>Cache TTL (seconds) the backend is using; the UI computes a "stale" badge as <c>now - snapshot.fetchedAt &gt; ttlSeconds</c>.</summary>
    public int TtlSeconds { get; init; }
    public List<QuotaSnapshot> Snapshots { get; init; } = [];
}
