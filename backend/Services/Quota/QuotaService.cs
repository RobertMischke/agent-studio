using System.Collections.Concurrent;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Quota;

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
    public QuotaReport GetWithBackgroundRefresh(CancellationToken ct = default)
    {
        foreach (var k in _probes.Keys)
        {
            var stale = !_cache.TryGetValue(k, out var s) || (DateTime.UtcNow - s.FetchedAt) > _ttl;
            if (stale) _ = RefreshAsync(k, ct);
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            var snap = await probe.ProbeAsync(cts.Token);
            _cache[cliType] = snap;
            PersistCache();
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota probe for {Cli} threw", cliType);
            var snap = new QuotaSnapshot { CliType = cliType, Error = ex.Message };
            _cache[cliType] = snap;
            PersistCache();
            return snap;
        }
        finally { sem.Release(); }
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
