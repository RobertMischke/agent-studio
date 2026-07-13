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
    private readonly ConcurrentDictionary<string, string> _latestVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private long _generation;

    public GenerationSingleFlightCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TValue GetOrCreate(string key, TimeSpan ttl, Func<TValue> valueFactory)
        => GetOrCreateVersioned(key, string.Empty, ttl, valueFactory);

    /// <summary>
    /// Caches one current version per logical key. A changed version starts a
    /// new single flight immediately without retaining every historical value.
    /// </summary>
    public TValue GetOrCreateVersioned(
        string key,
        string version,
        TimeSpan ttl,
        Func<TValue> valueFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(valueFactory);

        var generation = Volatile.Read(ref _generation);
        _latestVersions[key] = version;
        var now = _timeProvider.GetUtcNow();
        if (_values.TryGetValue(key, out var cached)
            && cached.Generation == generation
            && string.Equals(cached.Version, version, StringComparison.Ordinal)
            && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        // File-system paths cannot contain NUL, so this is an unambiguous
        // generation + caller-key composite while retaining the dictionary's
        // case-insensitive path semantics.
        var flightKey = $"{generation}\0{key}\0{version}";
        var candidate = new Lazy<TValue>(valueFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(flightKey, candidate);

        try
        {
            var value = flight.Value;
            if (generation == Volatile.Read(ref _generation)
                && _latestVersions.TryGetValue(key, out var latestVersion)
                && string.Equals(latestVersion, version, StringComparison.Ordinal))
            {
                _values[key] = new CacheEntry(
                    generation,
                    version,
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
        _latestVersions.Clear();
    }

    internal int ValueCount => _values.Count;

    private sealed record CacheEntry(
        long Generation,
        string Version,
        DateTimeOffset ExpiresAt,
        TValue Value);
}
