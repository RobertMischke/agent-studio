using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Run phases a <see cref="QuotaSnapshotEvent"/> can be emitted for. Kept as
/// literal strings so the on-disk field value is stable and greppable.
/// </summary>
public static class QuotaSnapshotPhases
{
    public const string Start = "start";
    public const string End = "end";
}

/// <summary>
/// One quota window projected onto a <see cref="QuotaSnapshotEvent"/>. A compact
/// copy of <see cref="QuotaWindow"/> with the fields that matter for later
/// analysis (usedPct + resetAt) plus the absolute counts when the CLI exposed
/// them.
/// </summary>
public sealed record QuotaSnapshotWindowEvent
{
    /// <summary>"5-hour" / "Weekly" / "Premium requests" / etc.</summary>
    public string Label { get; init; } = "";
    /// <summary>Percentage used, 0..100+.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UsedPct { get; init; }
    /// <summary>UTC timestamp when this window resets, when computable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ResetAt { get; init; }
    /// <summary>Original human-readable reset string from the CLI.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetLabel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Used { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Limit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }
}

/// <summary>
/// One compact quota-snapshot observation written to the run metadata / agent
/// message bus at run-start and run-end (AGT-2100). This is the payload shape of
/// the <c>kind:observation</c>, <c>topic:quota-snapshot</c> bus event: one line
/// of JSON per event, with stable field names so a downstream cap-forecast model
/// can consume the history without re-parsing.
///
/// <para>
/// The event mirrors the currently CACHED <see cref="QuotaSnapshot"/> for the CLI
/// that ran; it never forces a fresh probe. <see cref="SnapshotAgeSec"/> and
/// <see cref="Stale"/> record how old the cached reading was so a reader can tell
/// a fresh snapshot from a stale one (fetchedAt-Alter).
/// </para>
/// </summary>
public sealed record QuotaSnapshotEvent
{
    /// <summary><see cref="QuotaSnapshotPhases.Start"/> | <see cref="QuotaSnapshotPhases.End"/>.</summary>
    public string Phase { get; init; } = "";
    public string CliType { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingLevel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plan { get; init; }
    /// <summary>How the snapshot was sourced: "/usage" / "/status" / "footer".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JobId { get; init; }
    /// <summary>When the cached snapshot was probed. Null when no snapshot was cached.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FetchedAt { get; init; }
    /// <summary>Age of the cached snapshot in seconds at emit time. Null when missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SnapshotAgeSec { get; init; }
    /// <summary>Cache TTL the runner is using; a reader recomputes stale as age &gt; ttl.</summary>
    public int TtlSeconds { get; init; }
    /// <summary>True when the cached snapshot was older than the TTL at emit time.</summary>
    public bool Stale { get; init; }
    /// <summary>True when no quota snapshot was cached for the CLI at all.</summary>
    public bool Missing { get; init; }
    /// <summary>AGT-2064 trust flag copied through so a distorted reading is visible.</summary>
    public bool Suspicious { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuspiciousReason { get; init; }
    /// <summary>Probe error, when the last probe failed but partial data survived.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
    public IReadOnlyList<QuotaSnapshotWindowEvent> Windows { get; init; } = [];
}

/// <summary>
/// Pure builder for <see cref="QuotaSnapshotEvent"/>. No I/O, no clock of its own
/// (<paramref name="nowUtc"/> is injected) so the age / stale derivation is
/// directly unit-testable.
/// </summary>
public static class QuotaSnapshotEventBuilder
{
    public static QuotaSnapshotEvent Build(
        string phase,
        string cliType,
        string? model,
        string? thinkingLevel,
        QuotaSnapshot? snapshot,
        TimeSpan ttl,
        DateTime nowUtc,
        string? runId = null,
        string? jobId = null)
    {
        var ttlSec = (int)Math.Max(0, ttl.TotalSeconds);

        // No cached snapshot: still emit a truthful, minimal event so the
        // history records that the run happened with no quota reading to lean on
        // (rather than a silent gap).
        if (snapshot is null)
        {
            return new QuotaSnapshotEvent
            {
                Phase = phase,
                CliType = cliType,
                Model = model,
                ThinkingLevel = thinkingLevel,
                RunId = runId,
                JobId = jobId,
                TtlSeconds = ttlSec,
                Missing = true,
            };
        }

        var fetchedAt = DateTime.SpecifyKind(snapshot.FetchedAt, DateTimeKind.Utc);
        var ageSec = (long)Math.Max(0, (nowUtc - fetchedAt).TotalSeconds);
        var stale = ttlSec > 0 && ageSec > ttlSec;

        var windows = (snapshot.Windows ?? new List<QuotaWindow>())
            .Select(w => new QuotaSnapshotWindowEvent
            {
                Label = w.Label ?? "",
                UsedPct = w.UsedPct,
                ResetAt = w.ResetAt,
                ResetLabel = w.ResetLabel,
                Used = w.Used,
                Limit = w.Limit,
                Unit = w.Unit,
            })
            .ToList();

        return new QuotaSnapshotEvent
        {
            Phase = phase,
            CliType = string.IsNullOrWhiteSpace(snapshot.CliType) ? cliType : snapshot.CliType,
            Model = model,
            ThinkingLevel = thinkingLevel,
            Plan = snapshot.Plan,
            Source = snapshot.Source,
            RunId = runId,
            JobId = jobId,
            FetchedAt = fetchedAt,
            SnapshotAgeSec = ageSec,
            TtlSeconds = ttlSec,
            Stale = stale,
            Missing = false,
            Suspicious = snapshot.Suspicious,
            SuspiciousReason = snapshot.SuspiciousReason,
            Error = snapshot.Error,
            Windows = windows,
        };
    }

    /// <summary>One-line human summary for the bus <c>summary</c> field (&lt;= 280 chars).</summary>
    public static string Summarize(QuotaSnapshotEvent e)
    {
        var head = $"quota[{e.Phase}] {e.CliType}";
        if (e.Missing) return Cap($"{head}: no cached snapshot");

        string windowsText;
        if (e.Windows.Count == 0)
            windowsText = string.IsNullOrEmpty(e.Error) ? "no windows" : $"error: {e.Error}";
        else
            windowsText = string.Join(", ", e.Windows.Select(w =>
                w.UsedPct is { } p ? $"{w.Label} {p:0.#}%" : $"{w.Label} ?"));

        var freshness = e.Stale ? "stale" : "fresh";
        var suspicious = e.Suspicious ? ", suspicious" : string.Empty;
        return Cap($"{head}: {windowsText} (age {e.SnapshotAgeSec ?? 0}s, {freshness}{suspicious})");
    }

    private static string Cap(string s) => s.Length <= 280 ? s : s[..277] + "...";
}
