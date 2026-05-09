using System.Collections.Immutable;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// In-memory snapshot cache of <see cref="JobInfo"/> across all watch paths,
/// invalidated by <see cref="JobWatcherService"/> events and by direct
/// notifications from mutation services. The polled hot paths
/// (<c>/api/jobs</c>, <c>/api/jobs/grouped</c>, <c>/api/runner/status</c>,
/// <c>FindJob</c>, <c>GetJobDetail</c>, supervisor observations) all bottom
/// out in <see cref="JobScannerService.ScanAllJobs"/>; routing that call
/// through this cache turns each poll from an O(N) disk walk + JSON parse
/// into an O(1) reference return when nothing changed.
///
/// <para><b>Consistency model:</b> read-after-write is guaranteed for any
/// mutation that calls <see cref="Invalidate"/>. The FileSystemWatcher
/// signal (debounced 500 ms in <see cref="JobWatcherService"/>) covers
/// external changes - things touched outside the API. There is also a
/// safety re-scan TTL (default 30 s) so a missed watcher event cannot
/// produce an indefinitely stale view.</para>
///
/// <para><b>Concurrency:</b> the cache slot is an <see cref="ImmutableList{T}"/>
/// updated under a coarse lock; readers under the lock get a stable
/// snapshot. Refresh is single-flight: while one thread is rescanning,
/// other readers see the previous snapshot rather than queueing behind
/// the disk walk. This is intentional - the worst case is one extra
/// poll cycle returning slightly older data; correctness across mutations
/// is still guaranteed because <see cref="Invalidate"/> blocks the next
/// read into the rescan path.</para>
/// </summary>
public sealed class JobIndexCache
{
    private readonly JobScannerService _scanner;
    private readonly ILogger<JobIndexCache> _logger;
    private readonly TimeSpan _safetyTtl;

    // Cache slot: snapshot + when it was taken + whether a mutation/watcher
    // event marked it stale before the next read got there.
    private readonly Lock _lock = new();
    private ImmutableList<JobInfo> _snapshot = ImmutableList<JobInfo>.Empty;
    private DateTime _snapshotAtUtc = DateTime.MinValue;
    private bool _dirty = true;

    // Single-flight: while one thread refreshes, others return the previous
    // snapshot (correct enough; the next read after they're done sees the
    // fresh one).
    private int _refreshing;

    // Cheap diagnostics so a perf regression here is visible in /healthz or
    // a future debug endpoint without spinning up a profiler.
    public long Hits;
    public long Misses;
    public long ExternalInvalidations;
    public long MutationInvalidations;

    public JobIndexCache(JobScannerService scanner, ILogger<JobIndexCache> logger, IConfiguration config)
    {
        _scanner = scanner;
        _logger = logger;
        var ttlSec = int.TryParse(config["JobIndexCache:SafetyTtlSeconds"], out var v) ? v : 30;
        _safetyTtl = TimeSpan.FromSeconds(Math.Max(1, ttlSec));
    }

    /// <summary>
    /// Returns the cached snapshot of all jobs. If the cache is dirty or has
    /// aged past the safety TTL, refreshes by calling
    /// <see cref="JobScannerService.ScanAllJobsRaw"/> and replaces the
    /// snapshot atomically before returning.
    /// </summary>
    public ImmutableList<JobInfo> GetSnapshot()
    {
        // Fast path: not dirty, within TTL.
        lock (_lock)
        {
            if (!_dirty && DateTime.UtcNow - _snapshotAtUtc < _safetyTtl)
            {
                Interlocked.Increment(ref Hits);
                return _snapshot;
            }
        }

        // Single-flight: only one thread does the disk walk; the others
        // return the previous snapshot (correct enough because the dirty
        // flag stays set until the refresh completes, so the next reader
        // after that sees the fresh one).
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            lock (_lock) return _snapshot;
        }
        try
        {
            var fresh = _scanner.ScanAllJobsRaw();
            lock (_lock)
            {
                _snapshot = fresh.ToImmutableList();
                _snapshotAtUtc = DateTime.UtcNow;
                _dirty = false;
                Interlocked.Increment(ref Misses);
                return _snapshot;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// Marks the cache stale so the next <see cref="GetSnapshot"/> call
    /// rescans from disk. Called from JobWatcherService (external changes)
    /// and from mutation services (API-driven changes) so a write is always
    /// visible on the next read.
    /// </summary>
    public void Invalidate(InvalidationSource source = InvalidationSource.Mutation)
    {
        lock (_lock) { _dirty = true; }
        if (source == InvalidationSource.External)
            Interlocked.Increment(ref ExternalInvalidations);
        else
            Interlocked.Increment(ref MutationInvalidations);
    }

    /// <summary>
    /// Test / debug hook: forces a synchronous rescan and returns the new
    /// snapshot size. Useful in unit tests that want to assert "the cache
    /// reflects what we just wrote to disk" without racing the lazy path.
    /// </summary>
    public int ForceRefresh()
    {
        Invalidate(InvalidationSource.Mutation);
        return GetSnapshot().Count;
    }

    public enum InvalidationSource
    {
        /// <summary>FileSystemWatcher fired (something outside the API touched the workspace).</summary>
        External,
        /// <summary>An API mutation just wrote to disk and explicitly invalidated.</summary>
        Mutation,
    }
}
