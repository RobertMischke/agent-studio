using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentStudio.PublicDemo;

/// <summary>
/// The ephemeral viewer boundary. A visitor is handed an opaque, HttpOnly,
/// same-site, TLS-only cookie on first contact.
///
/// This is explicitly public authority, not a secret (dossier AGT-W34 §6): anyone
/// can obtain one, so it prevents nothing on its own. What it buys is a stable
/// subject for the request budget and for hub scoping, and a boundary that
/// expires and is never persisted - nothing about a visitor survives a reset or
/// a process restart.
/// </summary>
public sealed class PublicDemoViewerSessions(TimeProvider time, TimeSpan lifetime, int maxSessions)
{
    public const string CookieName = "asdemo_viewer";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public int Count => _lastSeen.Count;

    /// <summary>
    /// Returns the viewer id for this request, issuing a fresh one when the
    /// presented cookie is missing, malformed, or expired. <paramref name="issued"/>
    /// tells the caller whether a Set-Cookie is due.
    /// </summary>
    public string Resolve(string? presented, out bool issued)
    {
        var now = time.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(presented)
            && _lastSeen.TryGetValue(presented, out var seenAt)
            && now - seenAt < lifetime)
        {
            _lastSeen[presented] = now;
            issued = false;
            return presented;
        }

        Evict(now);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _lastSeen[id] = now;
        issued = true;
        return id;
    }

    public bool IsLive(string? presented)
        => !string.IsNullOrWhiteSpace(presented)
           && _lastSeen.TryGetValue(presented, out var seenAt)
           && time.GetUtcNow() - seenAt < lifetime;

    private void Evict(DateTimeOffset now)
    {
        foreach (var entry in _lastSeen)
        {
            if (now - entry.Value >= lifetime) _lastSeen.TryRemove(entry.Key, out _);
        }
        if (_lastSeen.Count < maxSessions) return;

        // Still at the ceiling after dropping expired entries: shed the least
        // recently seen visitors so a connection flood cannot grow this map.
        // Shed down to a headroom mark in one ordered pass rather than one entry
        // per request, so a sustained flood cannot turn every request into
        // another full sort of the map.
        var headroom = Math.Max(1, maxSessions / 10);
        var doomed = _lastSeen
            .OrderBy(entry => entry.Value)
            .Take(_lastSeen.Count - maxSessions + headroom)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var key in doomed) _lastSeen.TryRemove(key, out _);
    }
}
