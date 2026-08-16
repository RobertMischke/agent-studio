using System.Collections.Concurrent;

namespace AgentStudio.PublicDemo;

/// <summary>Fixed-window counter state for one visitor.</summary>
/// <param name="WindowStart">Start of the window the count belongs to.</param>
/// <param name="Count">Requests already admitted in that window.</param>
public readonly record struct PublicEdgeWindow(DateTimeOffset WindowStart, int Count);

/// <summary>
/// The pure fixed-window decision. Rate limiting is the abuse defense the
/// dossier assigns to the edge, because public read authority is available to
/// everyone and therefore cannot itself prevent scraping.
/// </summary>
public static class PublicEdgeRateLimitPolicy
{
    /// <summary>
    /// Advance one visitor's window. Returns the next state and whether this
    /// request is admitted. Rolling the window forward on expiry is what keeps
    /// the state bounded in time without a sweep.
    /// </summary>
    public static (PublicEdgeWindow Next, bool Allowed) Admit(
        PublicEdgeWindow current,
        DateTimeOffset now,
        TimeSpan window,
        int limit)
    {
        if (now - current.WindowStart >= window) return (new PublicEdgeWindow(now, 1), true);
        if (current.Count >= limit) return (current, false);
        return (current with { Count = current.Count + 1 }, true);
    }
}

/// <summary>
/// Bounded per-visitor request accounting for the public demo edge. Keyed by the
/// ephemeral viewer identity, which is public by design: it constrains cost per
/// browser session, it is not a credential and grants nothing.
///
/// <para>
/// The table is capped. When the cap is reached the limiter stops admitting new
/// identities for the remainder of the current window instead of growing without
/// bound, so a client that discards its cookie on every request cannot turn the
/// limiter itself into the memory-exhaustion path.
/// </para>
/// </summary>
public sealed class PublicEdgeRateLimiter(PublicEdgeContract contract, TimeProvider time)
{
    /// <summary>Distinct visitor identities tracked at once. Roughly one entry per active browser session.</summary>
    internal const int MaxTrackedVisitors = 20_000;

    private readonly ConcurrentDictionary<string, PublicEdgeWindow> _windows = new(StringComparer.Ordinal);

    public bool Admit(string visitorId)
    {
        var now = time.GetUtcNow();
        if (!_windows.ContainsKey(visitorId) && _windows.Count >= MaxTrackedVisitors)
        {
            PruneExpired(now);
            if (_windows.Count >= MaxTrackedVisitors) return false;
        }

        var admitted = false;
        _windows.AddOrUpdate(
            visitorId,
            _ =>
            {
                admitted = true;
                return new PublicEdgeWindow(now, 1);
            },
            (_, current) =>
            {
                var (next, allowed) = PublicEdgeRateLimitPolicy.Admit(current, now, contract.Window, contract.RequestsPerWindow);
                admitted = allowed;
                return next;
            });

        return admitted;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            if (now - window.WindowStart >= contract.Window) _windows.TryRemove(key, out _);
        }
    }
}
