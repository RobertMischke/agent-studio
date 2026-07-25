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
    int? RequestedTtlSeconds = null,
    string? RepositoryId = null,
    string? SourceRunAttemptId = null,
    string? IdempotencyKey = null)
{
    /// <summary>Authenticated task API client identity when acquisition came through a daemon claim.</summary>
    public string? ClientId { get; init; }
}

/// <summary>Heartbeat: extends the lease only when lease id + fencing token + runner still match the current holder.</summary>
public sealed record RunLeaseHeartbeatRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    int? RequestedTtlSeconds = null,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null);

/// <summary>Release: drops the lease only for the matching current holder; the fencing token keeps climbing for the next acquire.</summary>
public sealed record RunLeaseReleaseRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null);

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
    DateTime ExpiresAt,
    string? AttemptId = null,
    long AuthorityEpoch = 0)
{
    /// <summary>Most recent successful acquire/re-entry/renewal activity.</summary>
    public DateTime LastHeartbeatAt { get; init; } = AcquiredAt;
    public string? ClientId { get; init; }
}

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
    int? RequestedTtlSeconds = null,
    AgentStudio.Clients.HostTelemetrySample? Telemetry = null,
    int AvailableSlots = 1,
    int? ActiveSlots = null,
    string? IdempotencyKey = null,
    IReadOnlyList<string>? ActiveTaskKeys = null);

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
    string? Message = null,
    string? ProjectId = null,
    string? RepositoryUrl = null,
    string? DefaultBranch = null,
    string? TaskKind = null);

/// <summary>Fenced request for the server-rendered Epic decomposition prompt.</summary>
public sealed record RemoteEpicPlanningPromptRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string WorkingDirectory);

/// <summary>
/// The local and remote runners consume the same runtime prompt template and
/// project planning-model selection. The remote host receives only the fully
/// rendered prompt, never a second copy of the decomposition contract.
/// </summary>
public sealed record RemoteEpicPlanningPromptResponse(
    string Prompt,
    string? CliType,
    string? Model,
    string? ThinkingLevel);

/// <summary>
/// Fenced handoff from a standalone runner after its CLI exits. This is a
/// normal runner completion, not an out-of-band reconciliation: successful
/// outcomes enter auto-review and retain the regular agent-run timeline.
/// </summary>
public sealed record RemoteRunCompletionRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    string Outcome,
    string? Reason = null,
    string? Source = null,
    int? ExitCode = null,
    string? SalvageBranch = null,
    string? SalvageCommitSha = null,
    string? SalvageBranchUrl = null,
    string? ResultSha = null,
    string? AttemptChainId = null,
    string? SalvageResolution = null,
    string? SalvageLocalCommitSha = null,
    string? SalvageRecoveryBranch = null,
    string? SalvageRecoveryCommitSha = null,
    string? SalvageRecoveryBranchUrl = null,
    string? SalvageAuthoritativeBaseBranch = null,
    string? SalvageAuthoritativeBaseSha = null,
    string? Repository = null,
    // AGT-2178: Epic planning carries its decomposition output and read-only
    // mutation verdict additively; coding-task salvage fields above are
    // untouched (develop's 2177/2193 completion protocol remains the truth).
    IReadOnlyList<string>? OutputLines = null,
    bool SourceMutated = false,
    string? AttemptId = null,
    long? AuthorityEpoch = null,
    string? IdempotencyKey = null,
    IReadOnlyList<string>? GateItems = null,
    string? BaseSha = null,
    string? ImmutableResultRef = null,
    string? ArtifactManifestDigest = null);

public sealed record RemoteRunCompletionResponse(
    string TaskKey,
    string Outcome,
    string TargetState,
    string? Message = null,
    string? RunAttemptId = null,
    string? ReviewAttemptId = null,
    string? ReviewSubjectId = null,
    string? FailureClassification = null);
