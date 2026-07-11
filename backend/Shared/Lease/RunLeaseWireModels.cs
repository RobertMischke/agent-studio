namespace AgentStudio.Shared;

/// <summary>
/// Wire contracts for the fenced Runner ↔ Task Server run lease
/// (<c>/api/runner/lease</c>; parallel-task-execution.md §8.2C; ADR-0060). A
/// runner leases a task before it spawns a CLI, heartbeats while it works, and
/// releases when done. The server is the lease authority: on acquire it mints a
/// <see cref="RunLeaseInfoDto.LeaseId"/> and a monotonic
/// <see cref="RunLeaseInfoDto.FencingToken"/> per task, and every heartbeat /
/// release / state-affecting write must present the current token — a stale token
/// (after a TTL takeover raised the fence) is rejected. This is the productive
/// successor to the disk-backed <c>.pickup-lock.json</c> lease (ADR-0044), which
/// remains the same-machine pickup guard until the runner split cuts over.
///
/// <para>
/// The owner identity (runner id/name, host, pid, backend) travels in the request
/// because a remote runner's pid is meaningless to the server — the lease's TTL
/// plus fencing token, not the pid, decide takeover and staleness.
/// </para>
/// </summary>
public sealed record RunLeaseAcquireRequest(
    string TaskKey,
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null);

/// <summary>Heartbeat: extends the lease only when lease id + fencing token + runner still match the current holder.</summary>
public sealed record RunLeaseHeartbeatRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    int? RequestedTtlSeconds = null);

/// <summary>Release: drops the lease only for the matching current holder; the fencing token keeps climbing for the next acquire.</summary>
public sealed record RunLeaseReleaseRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId);

/// <summary>Wire projection of the server-held run-lease record.</summary>
public sealed record RunLeaseInfoDto(
    string TaskKey,
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    string LeaseId,
    long FencingToken,
    DateTime AcquiredAt,
    DateTime ExpiresAt);

/// <summary>
/// Result of a run-lease operation. <see cref="Outcome"/> is the discriminator
/// (<c>Acquired</c> / <c>AlreadyOwn</c> / <c>Held</c> / <c>Renewed</c> /
/// <c>Released</c> / <c>Expired</c> / <c>StaleToken</c> / <c>NotHeld</c> /
/// <c>Free</c> / <c>Invalid</c> / <c>TaskNotFound</c>); <see cref="Granted"/> is
/// the boolean callers branch on (true ⇒ this runner holds the lease and may
/// proceed).
/// </summary>
public sealed record RunLeaseResponse(
    string Outcome,
    bool Granted,
    RunLeaseInfoDto? Lease,
    string? Message = null);

/// <summary>
/// Remote daemon request for the next server-assigned, pickup-eligible card.
/// The server selects and leases the card as one fenced claim operation.
/// </summary>
public sealed record RunnerClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null);

/// <summary>Typed status handed back by one daemon pickup poll.</summary>
public enum RunnerClaimStatus
{
    Claimed,
    Empty,
    Invalid,
}

/// <summary>Result of one daemon pickup poll.</summary>
public sealed record RunnerClaimResponse(
    RunnerClaimStatus Status,
    string? TaskKey = null,
    string? JobId = null,
    string? ProjectName = null,
    RunLeaseInfoDto? Lease = null,
    string? Message = null);
