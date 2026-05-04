using System.Collections.Concurrent;

namespace CompanionRelay;

/// <summary>
/// In-memory state for one companion deployment: the latest snapshot the
/// processor pushed, plus a small queue of commands the PWA enqueued. No
/// persistence; a relay restart clears state and the next processor tick
/// repopulates the snapshot within one cycle.
/// </summary>
public sealed class RelayStore
{
    private readonly object _gate = new();
    private CompanionSnapshot? _snapshot;
    private DateTimeOffset? _lastSyncAt;

    private readonly ConcurrentQueue<CompanionCommand> _queue = new();

    public void StoreSnapshot(CompanionSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _lastSyncAt = DateTimeOffset.UtcNow;
        }
    }

    public CompanionSnapshot? GetSnapshot() { lock (_gate) return _snapshot; }
    public DateTimeOffset? GetLastSyncAt() { lock (_gate) return _lastSyncAt; }

    public CompanionCommand Enqueue(string kind, System.Text.Json.JsonElement payload)
    {
        var cmd = new CompanionCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = kind,
            Payload = payload,
        };
        _queue.Enqueue(cmd);
        return cmd;
    }

    /// <summary>
    /// Returns all currently queued commands. The processor sends back ack
    /// ids on its next sync and we drop those entries via <see cref="Drop"/>.
    /// We do not pop on drain so a flaky processor that crashes after pulling
    /// will see the same commands again on retry.
    /// </summary>
    public IReadOnlyList<CompanionCommand> Drain() => _queue.ToArray();

    public void Drop(IEnumerable<string> ids)
    {
        if (ids is null) return;
        var keep = new HashSet<string>(ids);
        // Concurrent queue has no remove-by-value; rebuild it. Volume is tiny.
        var copy = _queue.ToArray();
        while (_queue.TryDequeue(out _)) { }
        foreach (var c in copy) if (!keep.Contains(c.Id)) _queue.Enqueue(c);
    }

    public int PendingCount => _queue.Count;
}
