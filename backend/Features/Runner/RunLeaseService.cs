namespace AgentStudio.Runner;

/// <summary>
/// Server-authoritative, fenced <b>task-run</b> lease (parallel-task-execution.md
/// §8.2C; ADR-0060). Exactly one runner may hold the run lease for a task at a
/// time; a monotonically increasing <c>fencingToken</c> is minted per task on
/// every grant so a stale runner — one that missed heartbeats and lost the lease
/// to a takeover — has its later heartbeats, releases, and (via
/// <see cref="IsCurrent"/>) state-affecting writes rejected. This is the
/// split-brain guard §8.2C requires: TTL alone is not sufficient.
///
/// <para>
/// This is the productive successor to the disk-backed <c>.pickup-lock.json</c>
/// primitive (<see cref="PickupLockFile"/>, ADR-0044). It keeps the same
/// <see cref="DefaultTtl"/> (120s) so behaviour is comparable, but the lease
/// lives in the server's memory rather than a shared file, which is what the
/// multi-system runner split (ADR-0059) needs. Unlike the per-project integration
/// lease (<see cref="IntegrationLeaseService"/>) there is no queue: a contender
/// that loses the race is told the task is <c>Held</c> and does not wait — §8.2C
/// "two runner processes race the same ready task; only one gets a lease".
/// </para>
///
/// <para>
/// In-memory today: a server restart forgets leases, so takeover on restart is
/// immediate rather than gated by stored expiry. Persisting lease rows on the
/// shared Task Store (§8.2C "server restart preserves lease rows") is deferred to
/// the store-backed slice; this service is the fenced contract the store will
/// implement behind.
/// </para>
/// </summary>
public sealed class RunLeaseService
{
    /// <summary>Default lease TTL — 120s, matching the ADR-0044 pickup lease this supersedes.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(PickupLockFile.LeaseTtlSeconds);
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, RunLeaseSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RunLeaseService> _logger;
    private readonly Func<DateTime> _utcNow;

    public RunLeaseService(ILogger<RunLeaseService> logger, Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Acquire (or re-enter) the run lease for a task. Grants when no unexpired
    /// lease exists, minting a fresh lease id and the next fencing token. A live
    /// lease held by a <b>different</b> runner is rejected as <c>Held</c>; the same
    /// runner asking again is idempotent (<c>AlreadyOwn</c>, same lease id + token,
    /// TTL refreshed).
    /// </summary>
    public RunLeaseResponse TryAcquire(RunLeaseAcquireRequest request)
    {
        if (Blank(request.TaskKey) || Blank(request.RunnerId))
            return new RunLeaseResponse("Invalid", false, null, "TaskKey and RunnerId are required.");

        var now = _utcNow();
        var key = Normalize(request.TaskKey);
        var runnerId = Normalize(request.RunnerId);
        var ttl = NormalizeTtl(request.RequestedTtlSeconds);

        lock (_gate)
        {
            var slot = SlotFor(key);
            PruneExpired(slot, key, now);

            if (slot.Current is { } current)
            {
                if (SameRunner(current, runnerId))
                {
                    slot.Current = current with { ExpiresAt = now.Add(ttl), LastHeartbeatAt = now };
                    return new RunLeaseResponse("AlreadyOwn", true, ToDto(slot.Current));
                }

                return new RunLeaseResponse(
                    "Held",
                    false,
                    ToDto(current),
                    $"Task '{key}' is leased by runner '{current.RunnerId}' (token {current.FencingToken}) until {current.ExpiresAt:o}.");
            }

            var lease = Grant(slot, key, request, runnerId, ttl, now);
            _logger.LogInformation(
                "[run-lease] granted {TaskKey} to runner={RunnerId} lease={LeaseId} token={FencingToken} ttl={TtlSeconds}s",
                key, runnerId, lease.LeaseId, lease.FencingToken, (int)ttl.TotalSeconds);
            return new RunLeaseResponse("Acquired", true, ToDto(lease));
        }
    }

    /// <summary>
    /// Heartbeat: extend the current lease. Rejected as <c>Expired</c> when the TTL
    /// already lapsed, or <c>StaleToken</c> when the presented lease id / fencing
    /// token / runner id no longer matches the holder (a takeover happened).
    /// </summary>
    public RunLeaseResponse Renew(RunLeaseHeartbeatRequest request)
    {
        var invalid = ValidateReference(request.TaskKey, request.LeaseId, request.FencingToken, request.RunnerId);
        if (invalid != null) return invalid;

        var now = _utcNow();
        var key = Normalize(request.TaskKey);
        var ttl = NormalizeTtl(request.RequestedTtlSeconds);

        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null)
                return new RunLeaseResponse("NotHeld", false, null, "No run lease is currently held for this task.");

            if (slot.Current.ExpiresAt <= now)
            {
                var expired = slot.Current;
                slot.Last = expired;
                slot.LastState = "expired";
                slot.Current = null;
                _logger.LogWarning(
                    "[run-lease] heartbeat rejected expired {TaskKey} lease={LeaseId} token={FencingToken}",
                    key, expired.LeaseId, expired.FencingToken);
                return new RunLeaseResponse("Expired", false, ToDto(expired), "The run lease expired before the heartbeat arrived.");
            }

            if (!Matches(slot.Current, request.LeaseId, request.FencingToken, request.RunnerId))
            {
                _logger.LogWarning(
                    "[run-lease] heartbeat rejected stale {TaskKey} presented lease={LeaseId} token={FencingToken} runner={RunnerId}; current token={CurrentToken}",
                    key, request.LeaseId, request.FencingToken, Normalize(request.RunnerId), slot.Current.FencingToken);
                return new RunLeaseResponse("StaleToken", false, ToDto(slot.Current), "Lease id, fencing token, or runner id does not match the current holder.");
            }

            slot.Current = slot.Current with { ExpiresAt = now.Add(ttl), LastHeartbeatAt = now };
            return new RunLeaseResponse("Renewed", true, ToDto(slot.Current));
        }
    }

    /// <summary>
    /// Release the lease held by the matching runner. A stale token is rejected so
    /// a woken-up stale runner cannot clear the takeover holder's lease. The slot's
    /// fencing counter is retained so the next acquire is strictly higher.
    /// </summary>
    public RunLeaseResponse Release(RunLeaseReleaseRequest request)
    {
        var invalid = ValidateReference(request.TaskKey, request.LeaseId, request.FencingToken, request.RunnerId);
        if (invalid != null) return invalid;

        var key = Normalize(request.TaskKey);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null)
                return new RunLeaseResponse("NotHeld", false, null, "No run lease is currently held for this task.");

            if (!Matches(slot.Current, request.LeaseId, request.FencingToken, request.RunnerId))
                return new RunLeaseResponse("StaleToken", false, ToDto(slot.Current), "Lease id, fencing token, or runner id does not match the current holder.");

            var released = slot.Current;
            slot.Last = released;
            slot.LastState = "released";
            slot.Current = null;
            _logger.LogInformation(
                "[run-lease] released {TaskKey} from runner={RunnerId} lease={LeaseId} token={FencingToken}",
                key, released.RunnerId, released.LeaseId, released.FencingToken);
            return new RunLeaseResponse("Released", false, ToDto(released));
        }
    }

    /// <summary>Report the current holder (or <c>Free</c>) without mutating a live lease.</summary>
    public RunLeaseResponse Peek(string taskKey)
    {
        if (Blank(taskKey)) return new RunLeaseResponse("Invalid", false, null, "TaskKey is required.");
        var key = Normalize(taskKey);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot)) return new RunLeaseResponse("Free", false, null);
            PruneExpired(slot, key, _utcNow());
            return new RunLeaseResponse(slot.Current is null ? "Free" : "Held", false, ToDto(slot.Current));
        }
    }

    /// <summary>
    /// Read-side inspection that retains the most recent expired or released
    /// owner for health and historical attribution. It never grants authority:
    /// only <see cref="Peek"/> and <see cref="IsCurrent"/> describe a live lease.
    /// </summary>
    public RunLeaseInspection Inspect(string taskKey)
    {
        if (Blank(taskKey)) return new RunLeaseInspection("none", null);
        var key = Normalize(taskKey);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot)) return new RunLeaseInspection("none", null);
            PruneExpired(slot, key, _utcNow());
            return slot.Current is not null
                ? new RunLeaseInspection("active", ToDto(slot.Current))
                : new RunLeaseInspection(slot.Last is null ? "none" : slot.LastState, ToDto(slot.Last));
        }
    }

    /// <summary>
    /// The §8.2C write gate: a state-affecting write (log-chunk ingestion, timeline
    /// append, run completion, lane transition, integration, cleanup) is allowed
    /// only while this exact lease is still current. After a TTL takeover raised the
    /// fencing token, an old holder's token no longer matches, so this returns false
    /// and the caller rejects the stale write instead of corrupting task state.
    /// </summary>
    public bool IsCurrent(string taskKey, string leaseId, long fencingToken, string runnerId)
    {
        if (Blank(taskKey) || Blank(leaseId) || Blank(runnerId) || fencingToken <= 0) return false;
        var key = Normalize(taskKey);
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot) || slot.Current is null) return false;
            if (slot.Current.ExpiresAt <= _utcNow())
            {
                slot.Current = null;
                return false;
            }
            return Matches(slot.Current, leaseId, fencingToken, runnerId);
        }
    }

    private RunLeaseRecord Grant(RunLeaseSlot slot, string key, RunLeaseAcquireRequest request, string runnerId, TimeSpan ttl, DateTime now)
    {
        slot.LastFencingToken++;
        var lease = new RunLeaseRecord(
            key,
            runnerId,
            Normalize(request.RunnerName),
            Normalize(request.Hostname),
            request.Pid,
            Normalize(request.BackendName),
            Normalize(request.ClientId),
            Guid.NewGuid().ToString("N"),
            slot.LastFencingToken,
            now,
            now,
            now.Add(ttl));
        slot.Current = lease;
        slot.Last = lease;
        slot.LastState = "active";
        return lease;
    }

    private RunLeaseSlot SlotFor(string key)
    {
        if (!_slots.TryGetValue(key, out var slot))
        {
            slot = new RunLeaseSlot();
            _slots[key] = slot;
        }
        return slot;
    }

    private void PruneExpired(RunLeaseSlot slot, string key, DateTime now)
    {
        if (slot.Current is null || slot.Current.ExpiresAt > now) return;
        _logger.LogWarning(
            "[run-lease] expired {TaskKey} lease={LeaseId} token={FencingToken}; reclaimable",
            key, slot.Current.LeaseId, slot.Current.FencingToken);
        slot.Last = slot.Current;
        slot.LastState = "expired";
        slot.Current = null;
    }

    private static bool SameRunner(RunLeaseRecord lease, string runnerId)
        => string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal);

    private static bool Matches(RunLeaseRecord lease, string leaseId, long fencingToken, string runnerId)
        => string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
           && lease.FencingToken == fencingToken
           && string.Equals(lease.RunnerId, Normalize(runnerId), StringComparison.Ordinal);

    private static RunLeaseResponse? ValidateReference(string taskKey, string leaseId, long fencingToken, string runnerId)
    {
        if (Blank(taskKey) || Blank(leaseId) || Blank(runnerId) || fencingToken <= 0)
            return new RunLeaseResponse("Invalid", false, null, "TaskKey, LeaseId, FencingToken (> 0), and RunnerId are required.");
        return null;
    }

    private static TimeSpan NormalizeTtl(int? seconds)
    {
        var ttl = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultTtl;
        if (ttl < MinTtl) return MinTtl;
        if (ttl > MaxTtl) return MaxTtl;
        return ttl;
    }

    private static RunLeaseInfoDto? ToDto(RunLeaseRecord? lease) => lease is null
        ? null
        : new RunLeaseInfoDto(
            lease.TaskKey,
            lease.RunnerId,
            lease.RunnerName,
            lease.Hostname,
            lease.Pid,
            lease.BackendName,
            lease.LeaseId,
            lease.FencingToken,
            lease.AcquiredAt,
            lease.ExpiresAt) { LastHeartbeatAt = lease.LastHeartbeatAt, ClientId = string.IsNullOrWhiteSpace(lease.ClientId) ? null : lease.ClientId };

    private static string Normalize(string? value) => (value ?? "").Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private sealed record RunLeaseRecord(
        string TaskKey,
        string RunnerId,
        string RunnerName,
        string Hostname,
        int Pid,
        string BackendName,
        string ClientId,
        string LeaseId,
        long FencingToken,
        DateTime AcquiredAt,
        DateTime LastHeartbeatAt,
        DateTime ExpiresAt);

    private sealed class RunLeaseSlot
    {
        public RunLeaseRecord? Current { get; set; }
        public RunLeaseRecord? Last { get; set; }
        public string LastState { get; set; } = "none";
        public long LastFencingToken { get; set; }
    }
}

public sealed record RunLeaseInspection(string State, RunLeaseInfoDto? Lease);
