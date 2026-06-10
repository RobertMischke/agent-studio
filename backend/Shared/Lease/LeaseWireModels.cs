namespace AgentStudio.Shared;

/// <summary>
/// Wire contracts for the Runner ↔ Server lease API (Step 6). A Runner — local
/// today, potentially on another machine tomorrow — leases a task before it
/// spawns a CLI, heartbeats while it works, and releases when done. The server
/// is the lease authority; these DTOs are what crosses the HTTP boundary so the
/// same code path works whether the Runner is in-process or remote.
///
/// <para>
/// The owner identity (host / pid / backend) travels in the request because a
/// remote Runner's pid is meaningless to the server — the lease's TTL, not the
/// pid, is what the server uses to decide a remote lease has lapsed. See
/// <c>PickupLockFile</c> for the lease semantics this wraps.
/// </para>
/// </summary>
public sealed record LeaseAcquireRequest(
    string TaskKey,
    string Hostname,
    int Pid,
    string BackendName,
    string Role,
    int? BackendPort = null,
    string? ProjectName = null);

/// <summary>Heartbeat / release only need to identify the owner, not its full role.</summary>
public sealed record LeaseRenewRequest(string TaskKey, string Hostname, int Pid, string BackendName);

public sealed record LeaseReleaseRequest(string TaskKey, string Hostname, int Pid, string BackendName);

/// <summary>Wire projection of the on-disk lease record.</summary>
public sealed record LeaseInfoDto(
    string Hostname,
    int Pid,
    string BackendName,
    string Role,
    string? ProjectName,
    DateTime AcquiredAt,
    DateTime? ExpiresAt);

/// <summary>
/// Result of a lease operation. <see cref="Outcome"/> mirrors the lock
/// outcome (Acquired / Stale / AlreadyOwn / ForeignHeld / Renewed / NotOwner /
/// Released / TaskNotFound); <see cref="Granted"/> is the boolean the caller
/// usually branches on (true ⇒ this Runner holds the lease and may proceed).
/// </summary>
public sealed record LeaseResponse(
    string Outcome,
    bool Granted,
    LeaseInfoDto? Lease,
    string? Message = null);
