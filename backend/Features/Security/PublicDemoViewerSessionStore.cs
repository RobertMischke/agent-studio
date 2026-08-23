using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentStudio.Security;

/// <summary>
/// Issues and tracks the ephemeral public-demo viewer session (W34 §6
/// "Visitor authorization"). The dossier is explicit that this session is
/// public bookkeeping, not a secret: anyone can obtain one by making a
/// request. Its job is to give the edge and <see cref="TaskHub"/> a cheap,
/// consistent way to tell "a browser that came through our own edge" apart
/// from a forged direct caller, and to bound in-memory growth with a sliding
/// expiry rather than persisting anything about the visitor.
/// </summary>
public sealed class PublicDemoViewerSessionStore(TimeProvider? timeProvider = null)
{
    public const string CookieName = "agentstudio-demo-viewer";

    // Sized for a demo VM: a burst well past normal traffic still fits in
    // memory, and Sweep() below reclaims expired entries opportunistically
    // instead of running a background timer for a value this small.
    private const int SweepThreshold = 5000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public string Issue()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _sessions[id] = _time.GetUtcNow() + SessionLifetime;
        if (_sessions.Count > SweepThreshold) Sweep();
        return id;
    }

    /// <summary>Refreshes the sliding expiry when <paramref name="id"/> is a live session.</summary>
    public bool Touch(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!_sessions.TryGetValue(id, out var expiresAt)) return false;
        var now = _time.GetUtcNow();
        if (expiresAt < now)
        {
            _sessions.TryRemove(id, out _);
            return false;
        }
        _sessions[id] = now + SessionLifetime;
        return true;
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, expiresAt) in _sessions)
            if (expiresAt < now) _sessions.TryRemove(id, out _);
    }
}
