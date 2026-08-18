using System.Collections.Concurrent;

namespace AgentStudio.PublicDemo;

/// <summary>A viewer's rolling request window. Immutable so the store can swap it atomically.</summary>
public readonly record struct PublicDemoRateWindow(long StartedAtTicks, int Count);

/// <summary>
/// The pure half of rate limiting: given the current window and the clock, decide
/// whether the request fits and what the next window looks like. Keeping the roll
/// decision here means the matrix (fresh window, inside window, exactly at the
/// limit, window expiry) is testable without waiting on a real clock.
/// </summary>
public static class PublicDemoRateLimitPolicy
{
    public static (PublicDemoRateWindow Next, bool Allowed) Evaluate(
        PublicDemoRateWindow current,
        long nowTicks,
        long windowTicks,
        int limit)
    {
        if (limit <= 0) return (current, false);

        var expired = nowTicks - current.StartedAtTicks >= windowTicks;
        if (expired) return (new PublicDemoRateWindow(nowTicks, 1), true);

        return current.Count >= limit
            ? (current, false)
            : (current with { Count = current.Count + 1 }, true);
    }
}

/// <summary>
/// Per-viewer fixed-window request budget. Bounded by construction: the map never
/// exceeds the configured viewer ceiling, and expired windows are dropped on the
/// sweep so an abusive client cannot grow the edge's memory.
/// </summary>
public sealed class PublicDemoRequestBudget(TimeProvider time, int requestsPerMinute, int maxTrackedViewers)
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly ConcurrentDictionary<string, PublicDemoRateWindow> _windows = new(StringComparer.Ordinal);

    public bool TryConsume(string viewerId)
    {
        var now = time.GetUtcNow().UtcTicks;
        if (_windows.Count >= maxTrackedViewers && !_windows.ContainsKey(viewerId)) Sweep(now);

        var allowed = false;
        _windows.AddOrUpdate(
            viewerId,
            _ =>
            {
                allowed = true;
                return new PublicDemoRateWindow(now, 1);
            },
            (_, current) =>
            {
                var (next, verdict) = PublicDemoRateLimitPolicy.Evaluate(
                    current, now, WindowTicks, requestsPerMinute);
                allowed = verdict;
                return next;
            });
        return allowed;
    }

    private void Sweep(long nowTicks)
    {
        foreach (var entry in _windows)
        {
            if (nowTicks - entry.Value.StartedAtTicks >= WindowTicks)
                _windows.TryRemove(entry.Key, out _);
        }
    }
}
