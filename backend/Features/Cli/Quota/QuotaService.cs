using System.Collections.Concurrent;

namespace AgentStudio.Cli;

/// <summary>
/// Orchestrates the per-CLI <see cref="IQuotaProbe"/> instances:
/// <list type="bullet">
///   <item>In-memory cache keyed by CLI type, with a TTL (default 10 min).</item>
///   <item>Stale-while-revalidate: cached snapshot is returned immediately, a background re-probe
///         updates the cache when the entry is stale and not currently being refreshed.</item>
///   <item><see cref="ForceRefreshAsync"/> awaits the next probe and replaces the cached value.</item>
///   <item>Concurrent re-probes for the same CLI are coalesced via per-CLI locks.</item>
/// </list>
/// Probing is expensive (each call spawns a CLI in a PTY for several seconds), so callers
/// should rely on the cache and only force-refresh on user intent or after a job exits.
/// </summary>
public sealed class QuotaService
{
    private readonly ILogger<QuotaService> _logger;
    private readonly IReadOnlyDictionary<string, IQuotaProbe> _probes;
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly TimeSpan _ttl;
    private readonly QuotaCacheStore _store;

    public QuotaService(
        ILogger<QuotaService> logger,
        IEnumerable<IQuotaProbe> probes,
        IConfiguration configuration,
        QuotaCacheStore store)
    {
        _logger = logger;
        _probes = probes.ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
        var ttlSec = int.TryParse(configuration["Quota:TtlSeconds"], out var t) ? t : 600;
        _ttl = TimeSpan.FromSeconds(ttlSec);
        _store = store;

        // Hydrate the in-memory cache from disk on startup so the
        // header / strip have something to render before the first
        // probe completes (probes take 30+ seconds per CLI). Stale
        // detection is the consumer's responsibility via TtlSeconds.
        try
        {
            foreach (var snap in _store.Read())
            {
                if (!string.IsNullOrWhiteSpace(snap.CliType)) _cache[snap.CliType] = snap;
            }
            _logger.LogInformation("Hydrated quota cache from disk ({Count} snapshots).", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate quota cache from disk");
        }
    }

    public IReadOnlyCollection<string> Probes => _probes.Keys.ToList();

    /// <summary>Cache TTL exposed so callers can render a "stale" badge.</summary>
    public TimeSpan Ttl => _ttl;

    public QuotaReport GetCached()
    {
        return new QuotaReport
        {
            TtlSeconds = (int)_ttl.TotalSeconds,
            Snapshots = _probes.Keys
                .Select(k => _cache.TryGetValue(k, out var s) ? s : new QuotaSnapshot { CliType = k })
                .ToList()
        };
    }

    /// <summary>
    /// Returns the in-memory snapshot for one CLI without triggering any
    /// refresh. Used by the cap-enforcement code path which must be cheap and
    /// non-blocking - it runs on every pickup tick.
    /// </summary>
    public QuotaSnapshot? GetCachedFor(string cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        return _cache.TryGetValue(cliType, out var s) ? s : null;
    }

    /// <summary>
    /// Returns the cached snapshot for every probe immediately, kicking off a background
    /// re-probe for any entry that is missing or older than the TTL.
    /// </summary>
    /// <remarks>
    /// AGT-2679: the refresh deliberately does NOT observe the caller's
    /// <paramref name="ct"/>. This is a GET-serving path, so that token is the
    /// request's <c>RequestAborted</c>; wiring it into a fire-and-forget probe meant
    /// every completed request cancelled the probe it had just started, and the
    /// resulting <c>TaskCanceledException</c> was cached as the operator-facing
    /// error "A task was canceled.". A background refresh outlives the request that
    /// triggered it and is bounded by its own budget in <see cref="ProbeOnceAsync"/>.
    ///
    /// <c>Task.Run</c> is likewise load-bearing: <see cref="RefreshAsync"/> runs
    /// synchronously until the PTY spawn, and that prefix includes
    /// <c>TestCliPath()</c>, which shells out to <c>&lt;cli&gt; --version</c> and blocks
    /// up to 5 s per CLI. On the request thread that turned a "serve from cache"
    /// GET into a multi-second (worst case: multi-CLI) stall.
    /// </remarks>
    public QuotaReport GetWithBackgroundRefresh(CancellationToken ct = default)
    {
        foreach (var k in _probes.Keys)
        {
            var stale = !_cache.TryGetValue(k, out var s) || (DateTime.UtcNow - s.FetchedAt) > _ttl;
            if (stale) _ = Task.Run(() => RefreshAsync(k, CancellationToken.None), CancellationToken.None);
        }
        return GetCached();
    }

    /// <summary>Force a re-probe of every CLI and await all of them.</summary>
    public async Task<QuotaReport> RefreshAllAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(_probes.Keys.Select(k => RefreshAsync(k, ct)));
        return GetCached();
    }

    public async Task<QuotaSnapshot?> RefreshAsync(string cliType, CancellationToken ct = default)
    {
        if (!_probes.TryGetValue(cliType, out var probe)) return null;
        var sem = _locks.GetOrAdd(cliType, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
        {
            // Another probe for this CLI is already running — let it win, return cached.
            _cache.TryGetValue(cliType, out var existing);
            return existing;
        }
        try
        {
            _cache.TryGetValue(cliType, out var previous);
            var snap = await ProbeOnceAsync(probe, ct);

            // A probe that came back empty-with-an-error did not throw, but it also
            // learned nothing. Treat it exactly like a thrown failure so the
            // operator keeps the last-good numbers instead of watching the display
            // blank out (AGT-2679).
            if (snap.Windows.Count == 0 && !string.IsNullOrEmpty(snap.Error))
            {
                var degraded = QuotaDegradationPolicy.Degrade(
                    previous, cliType, snap.Error, snap.CliVersion ?? ProbeVersion(probe), DateTime.UtcNow);
                LogDegraded(cliType, degraded);
                _cache[cliType] = degraded;
                PersistCache();
                return degraded;
            }

            snap = await ReconcileSuspiciousDropAsync(cliType, probe, previous, snap, ct);
            snap = QuotaWindowProjection.AnchorWindowStarts(previous, snap, DateTime.UtcNow);
            _cache[cliType] = snap;
            PersistCache();
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota probe for {Cli} threw", cliType);
            // Degrade rather than replace: the last-good windows are still the best
            // information available, and a bare exception message is not an
            // operator-facing sentence (AGT-2679). Degrade also latches a prior
            // suspicious flag, so a probe that fails right after a ground-truth
            // invalidation (AGT-2064) cannot silently re-open the admission gate.
            _cache.TryGetValue(cliType, out var prior);
            var version = ProbeVersion(probe);
            var snap = QuotaDegradationPolicy.Degrade(
                prior, cliType, QuotaDegradationPolicy.DescribeFailure(ex, cliType, version),
                version, DateTime.UtcNow);
            LogDegraded(cliType, snap);
            _cache[cliType] = snap;
            PersistCache();
            return snap;
        }
        finally { sem.Release(); }
    }

    /// <summary>
    /// Run one probe under a hard deadline.
    /// </summary>
    /// <remarks>
    /// AGT-2679: the deadline used to be a flat 45 s, but the codex step sequence
    /// can legitimately take ~57 s when its pattern waits all run to timeout (a
    /// slow CLI start, or a TUI that changed its startup screen). The probe was
    /// therefore killed mid-step and reported a bare cancellation rather than a
    /// parse result. Each probe now publishes its own worst case via
    /// <see cref="IQuotaProbe.BudgetMs"/>; the grace factor covers PTY spawn and
    /// scheduling overhead on top of the step waits themselves.
    /// </remarks>
    private static async Task<QuotaSnapshot> ProbeOnceAsync(IQuotaProbe probe, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(probe.BudgetMs + ProbeGraceMs));
        return await probe.ProbeAsync(cts.Token);
    }

    /// <summary>Headroom over a probe's declared step budget for spawn + scheduling overhead.</summary>
    private const int ProbeGraceMs = 10_000;

    private static string? ProbeVersion(IQuotaProbe probe)
        => probe is QuotaProbeBase b ? b.LastCliVersion : null;

    private void LogDegraded(string cliType, QuotaSnapshot snap)
    {
        if (snap.Stale)
        {
            _logger.LogWarning(
                "quota_probe_degraded cli={Cli} cliVersion={Version} lastGoodAt={LastGoodAt}: serving last-good windows, error={Error}",
                cliType, snap.CliVersion ?? "<unknown>", snap.LastGoodAt, snap.Error);
        }
        else
        {
            _logger.LogWarning(
                "quota_probe_failed cli={Cli} cliVersion={Version}: no prior snapshot to fall back on, error={Error}",
                cliType, snap.CliVersion ?? "<unknown>", snap.Error);
        }
    }

    /// <summary>
    /// AGT-2064 plausibility gate. A single probe that shows a window jumping
    /// DOWN by more than the threshold with no reset to explain it is not
    /// trusted on its own: re-probe immediately and only accept the drop when a
    /// second, independent measurement agrees. If the confirmation disagrees,
    /// the first reading was a transient glitch - keep the previous
    /// (still-blocking) value and flag it suspicious so the admission gate stays
    /// conservative until a clean reading arrives.
    /// </summary>
    private async Task<QuotaSnapshot> ReconcileSuspiciousDropAsync(
        string cliType, IQuotaProbe probe, QuotaSnapshot? previous, QuotaSnapshot candidate, CancellationToken ct)
    {
        var suspicion = QuotaPlausibilityGate.Evaluate(previous, candidate, DateTime.UtcNow);
        if (!suspicion.Suspicious) return candidate;

        _logger.LogWarning(
            "quota_snapshot_suspicious cli={Cli} reason={Reason}: re-probing to confirm before trusting the drop",
            cliType, suspicion.Reason);

        var confirm = await ProbeOnceAsync(probe, ct);
        if (QuotaPlausibilityGate.AreConsistent(candidate, confirm))
        {
            _logger.LogInformation(
                "quota_snapshot_confirmed cli={Cli}: two consistent probes agree, accepting the new value", cliType);
            return confirm;
        }

        _logger.LogWarning(
            "quota_snapshot_glitch_discarded cli={Cli} reason={Reason}: confirmation probe disagreed, holding prior snapshot (suspicious) so admission stays conservative",
            cliType, suspicion.Reason);
        return (previous ?? candidate) with
        {
            Suspicious = true,
            SuspiciousReason = suspicion.Reason,
            FetchedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Ground-truth override (AGT-2064): a live launch just died with a
    /// usage-limit error, which is proof the cached snapshot is wrong no matter
    /// how green it looks - the error text is the evidence. Flag the cached
    /// snapshot suspicious immediately so the admission gate stops trusting it,
    /// then re-probe right now instead of waiting out the (10-minute) TTL.
    /// Returns the re-probe task so callers that care (tests) can await it;
    /// the runner fires it and forgets.
    /// </summary>
    public Task InvalidateForGroundTruthLimit(string cliType, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return Task.CompletedTask;

        var existing = _cache.TryGetValue(cliType, out var s) ? s : new QuotaSnapshot { CliType = cliType };
        _cache[cliType] = existing with
        {
            CliType = cliType,
            Suspicious = true,
            SuspiciousReason = reason,
            FetchedAt = DateTime.UtcNow
        };
        PersistCache();
        _logger.LogWarning(
            "quota_snapshot_invalidated cli={Cli} reason={Reason}: launch hit a usage limit, re-probing now (bypassing TTL)",
            cliType, reason);

        return RefreshAsync(cliType, ct);
    }

    /// <summary>
    /// Push the current in-memory cache to disk. Best-effort and async-
    /// safe; failures are logged inside the store and never thrown.
    /// </summary>
    private void PersistCache()
    {
        try { _store.Write(_cache.Values); }
        catch (Exception ex) { _logger.LogDebug(ex, "Quota cache persist failed"); }
    }
}
