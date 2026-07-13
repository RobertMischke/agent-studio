using System.Collections.Concurrent;

namespace AgentStudio.Shared;

/// <summary>
/// Small synchronous single-flight cache for expensive read projections. A
/// cache miss is computed once per key and generation; concurrent callers wait
/// for that same value instead of starting duplicate external work.
/// </summary>
internal sealed class GenerationSingleFlightCache<TValue>
{
    private readonly ConcurrentDictionary<string, CacheEntry> _values =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<TValue>> _flights =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private long _generation;

    public GenerationSingleFlightCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TValue GetOrCreate(string key, TimeSpan ttl, Func<TValue> valueFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueFactory);

        var generation = Volatile.Read(ref _generation);
        var now = _timeProvider.GetUtcNow();
        if (_values.TryGetValue(key, out var cached)
            && cached.Generation == generation
            && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        // File-system paths cannot contain NUL, so this is an unambiguous
        // generation + caller-key composite while retaining the dictionary's
        // case-insensitive path semantics.
        var flightKey = $"{generation}\0{key}";
        var candidate = new Lazy<TValue>(valueFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(flightKey, candidate);

        try
        {
            var value = flight.Value;
            if (generation == Volatile.Read(ref _generation))
            {
                _values[key] = new CacheEntry(
                    generation,
                    _timeProvider.GetUtcNow().Add(ttl),
                    value);
            }
            return value;
        }
        finally
        {
            // Only the owner recorded for this generation may remove the
            // flight. The cache value is published before this removal, so a
            // following caller cannot slip through into a duplicate compute.
            if (_flights.TryGetValue(flightKey, out var current)
                && ReferenceEquals(current, flight))
            {
                _flights.TryRemove(flightKey, out _);
            }
        }
    }

    /// <summary>
    /// Starts a new generation. Already-running readers may finish with their
    /// old snapshot, but they cannot republish it into the new generation.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _values.Clear();
    }

    private sealed record CacheEntry(long Generation, DateTimeOffset ExpiresAt, TValue Value);
}
