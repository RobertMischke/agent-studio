using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public sealed class ProviderLimitRegistry
{
    private readonly ConcurrentDictionary<string, ProviderLimitDetection> _limits =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderLimitDetection Record(ProviderLimitDetection limit)
    {
        _limits.AddOrUpdate(limit.Provider, limit, (_, current) =>
            current.RetryAt >= limit.RetryAt ? current : limit);
        return _limits[limit.Provider];
    }

    public ProviderLimitDetection? Current(string? provider, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(provider) || !_limits.TryGetValue(provider, out var limit))
            return null;
        if (limit.RetryAt > (nowUtc ?? DateTime.UtcNow).ToUniversalTime()) return limit;
        _limits.TryRemove(provider, out _);
        return null;
    }

    public bool Clear(string? provider)
        => !string.IsNullOrWhiteSpace(provider) && _limits.TryRemove(provider, out _);

    public IReadOnlyList<ProviderLimitDetection> Snapshot(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        foreach (var key in _limits.Keys) _ = Current(key, now);
        return _limits.Values.OrderBy(item => item.Provider, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
