using System.Collections.Concurrent;

namespace OrchestratorApi.Services.Projection;

/// <summary>
/// In-memory snapshot store for projected conversations.
///
/// The cache key is the job id; the validation key is the per-source mtime
/// dictionary the projector built when it produced the snapshot. A hit is
/// only served when every recorded source still has the same mtime; otherwise
/// the projector re-runs.
///
/// Eviction is LRU on count (see <see cref="Capacity"/>); per-job memory is
/// bounded by the source files themselves, not by the cache.
/// </summary>
public sealed class ConversationCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private long _accessTick;

    public int Capacity { get; }
    public int Count => _entries.Count;

    public ConversationCache(int capacity = 50)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public bool TryGet(
        string jobId,
        IReadOnlyDictionary<string, DateTime> currentMTimes,
        out IReadOnlyList<ProjectedEvent> events)
    {
        events = Array.Empty<ProjectedEvent>();
        if (!_entries.TryGetValue(jobId, out var entry)) return false;
        if (!MTimesMatch(entry.SourceMTimes, currentMTimes)) return false;
        entry.LastAccessTick = Interlocked.Increment(ref _accessTick);
        events = entry.Events;
        return true;
    }

    public void Set(
        string jobId,
        IReadOnlyList<ProjectedEvent> events,
        IReadOnlyDictionary<string, DateTime> sourceMTimes)
    {
        var entry = new CacheEntry
        {
            Events = events,
            SourceMTimes = new Dictionary<string, DateTime>(sourceMTimes, StringComparer.Ordinal),
            LastAccessTick = Interlocked.Increment(ref _accessTick)
        };
        _entries[jobId] = entry;
        EvictIfNeeded();
    }

    public void Invalidate(string jobId) => _entries.TryRemove(jobId, out _);

    public void Clear() => _entries.Clear();

    private static bool MTimesMatch(
        IReadOnlyDictionary<string, DateTime> a,
        IReadOnlyDictionary<string, DateTime> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v)) return false;
            if (v != kv.Value) return false;
        }
        return true;
    }

    private void EvictIfNeeded()
    {
        while (_entries.Count > Capacity)
        {
            string? coldestKey = null;
            long coldestTick = long.MaxValue;
            foreach (var kv in _entries)
            {
                if (kv.Value.LastAccessTick < coldestTick)
                {
                    coldestTick = kv.Value.LastAccessTick;
                    coldestKey = kv.Key;
                }
            }
            if (coldestKey is null) return;
            _entries.TryRemove(coldestKey, out _);
        }
    }

    private sealed class CacheEntry
    {
        public IReadOnlyList<ProjectedEvent> Events { get; init; } = Array.Empty<ProjectedEvent>();
        public IReadOnlyDictionary<string, DateTime> SourceMTimes { get; init; }
            = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        public long LastAccessTick;
    }
}
