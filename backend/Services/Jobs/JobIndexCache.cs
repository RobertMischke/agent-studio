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

    // Single-flight: while one thread refreshes, others wait briefly for it to
    // finish so that a post-Invalidate read always sees the fresh snapshot.
    // A round counter lets waiters detect completion without holding a lock.
    private int _refreshing;
    private long _scanRound;

    // Invalidation generation counter. Incremented on every Invalidate so the
    // refresher can detect "did a mutation land while my disk walk was in
    // flight?" — captured before ScanAllJobsRaw, compared after. When the
    // counter has advanced, the just-read snapshot is racy relative to that
    // mutation: we install the snapshot (it's still better than nothing) but
    // leave _dirty=true so the very next read forces another refresh that
    // observes the post-mutation state. Without this guard, a torn read can
    // overwrite a true invalidation with stale data and the cache happily
    // serves the stale snapshot until the next external watcher event or the
    // 30s safety TTL, producing the "optimistic reorder reverts to the old
    // order after the next poll" symptom.
    private long _invalidationGen;

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
        // Bounded retry: if a waiter wakes onto a racy in-flight snapshot
        // (_dirty=true), it must re-run the refresh path so the mutation that
        // triggered the invalidation is observable. Without the loop, a
        // mutation that lands during an unrelated polling refresh leaves the
        // very next reader (e.g. POST /attachments right after CreateJob)
        // staring at the pre-mutation snapshot and 400-ing as "Job not found".
        // 4 retries is plenty: a sustained write storm would still terminate
        // the loop, and the worst-case wall time stays bounded at ~4 * 500 ms.
        for (var attempt = 0; attempt < 4; attempt++)
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

            // Single-flight: only one thread does the disk walk. Concurrent readers
            // wait up to 500 ms for it to finish so that a post-Invalidate read
            // always sees the fresh snapshot (read-after-write guarantee). Disk
            // rescans typically finish in <50 ms; the timeout is a safety valve.
            var roundBefore = Volatile.Read(ref _scanRound);
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref _scanRound) != roundBefore, 500);
                lock (_lock)
                {
                    // If the in-flight refresh resolved everything, return.
                    // If a mutation landed during it, _dirty is still true and
                    // the snapshot the refresher installed is racy w.r.t. that
                    // mutation - loop to drive a fresh scan ourselves.
                    if (!_dirty) return _snapshot;
                }
                continue;
            }
            try
            {
                // Capture the invalidation generation BEFORE the disk walk. Any
                // Invalidate() that lands while ScanAllJobsRaw is in flight bumps
                // the counter, so when we take the lock below we can tell whether
                // our just-read snapshot is racy. Without this, a mutation that
                // happens during the disk walk gets stomped by `_dirty = false`
                // and the cache serves stale data for the rest of the safety TTL.
                var genBefore = Volatile.Read(ref _invalidationGen);
                var fresh = _scanner.ScanAllJobsRaw();
                lock (_lock)
                {
                    _snapshot = fresh.ToImmutableList();
                    _snapshotAtUtc = DateTime.UtcNow;
                    // If no invalidation landed during the disk walk, the snapshot
                    // is authoritative. If one did, leave _dirty=true so the next
                    // reader rescans and observes the post-mutation state.
                    if (Volatile.Read(ref _invalidationGen) == genBefore)
                    {
                        _dirty = false;
                    }
                    Interlocked.Increment(ref Misses);
                    return _snapshot;
                }
            }
            finally
            {
                Interlocked.Increment(ref _scanRound);
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }
        // Fallthrough: bounded retries exhausted under a storm. Return whatever
        // we have rather than blocking forever; the next read will rescan.
        lock (_lock) return _snapshot;
    }

    /// <summary>
    /// Marks the cache stale so the next <see cref="GetSnapshot"/> call
    /// rescans from disk. Called from JobWatcherService (external changes)
    /// and from mutation services (API-driven changes) so a write is always
    /// visible on the next read.
    /// </summary>
    public void Invalidate(InvalidationSource source = InvalidationSource.Mutation)
    {
        // Bump the invalidation generation BEFORE setting _dirty so the
        // refresher's post-scan check (see GetSnapshot) sees a strictly
        // higher value than its captured `genBefore`. Without the bump,
        // a concurrent refresher could observe the same generation it
        // captured before the disk walk and still clear _dirty.
        Interlocked.Increment(ref _invalidationGen);
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
