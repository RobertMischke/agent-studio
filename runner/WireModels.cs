namespace AgentRunner;

// The small JSON wire contract the standalone runner (RM-5, Runner-Split C)
// exchanges with the local Studio Task Server. It is a deliberate re-declaration
// of the server-side records (backend/Shared/Lease/*, backend/Shared/Models/*)
// rather than a project reference: the runner ships to a different host than the
// server, so it must not drag the whole backend across the wire. Only the fields
// this MVP actually reads or writes are modelled here.
//
// The server serialises with the ASP.NET "web" defaults (camelCase names,
// case-insensitive binding), so plain PascalCase records serialise/deserialise
// cleanly in both directions via System.Text.Json with camelCase policy.

/// <summary>
/// Runner -> Server: register the runner as a client identity
/// (/api/clients/register). The server's X-Client-Id middleware rejects every
/// mutation (lease, log, artifact, completion) from an unregistered id
/// with 401 <c>client-unknown</c>, so the runner must register before it writes.
/// Registration is idempotent on <see cref="DisplayName"/> and the open-path
/// carve-out means it needs no prior identity itself.
/// </summary>
public sealed record ClientRegisterRequest(string DisplayName, string? Kind = null, string? Notes = null);

public sealed record RunnerGitCapabilityRequest(string Status, string? Detail, DateTime CheckedAt);

/// <summary>Server projection of a registered client identity; only <see cref="Id"/> is used (as the X-Client-Id).</summary>
public sealed record ClientRegisterResponse(string Id, string DisplayName, string Kind);

/// <summary>
/// Server response from GET /api/clients/{id}. The runner only needs the
/// canonical id and lifecycle kind to prove that a configured identity exists
/// and is still active before it starts issuing mutations under that id.
/// </summary>
public sealed record ClientIdentityDetail(ClientIdentitySummary Identity);

public sealed record ClientIdentitySummary(string Id, string Kind);

/// <summary>Runner -> Server: acquire the fenced run lease for a task (/api/runner/lease/acquire).</summary>
public sealed record RunLeaseAcquireRequest(
    string TaskKey,
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null);

/// <summary>Runner -> Server: heartbeat to extend the lease (/api/runner/lease/renew).</summary>
public sealed record RunLeaseHeartbeatRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    int? RequestedTtlSeconds = null);

/// <summary>Runner -> Server: drop the lease when the run ends (/api/runner/lease/release).</summary>
public sealed record RunLeaseReleaseRequest(
    string TaskKey,
    string LeaseId,
    long FencingToken,
    string RunnerId);

/// <summary>Server projection of the current lease holder + fencing token.</summary>
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
/// Server reply to any lease operation. <see cref="Granted"/> is the boolean the
/// runner branches on (true means this runner holds the lease and may proceed);
/// <see cref="Outcome"/> is the discriminator used only for logging.
/// </summary>
public sealed record RunLeaseResponse(
    string Outcome,
    bool Granted,
    RunLeaseInfoDto? Lease,
    string? Message = null);

public sealed record RunnerClaimRequest(
    string RunnerId,
    string RunnerName,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null,
    HostTelemetrySample? Telemetry = null,
    int AvailableSlots = 1);

/// <summary>Thirty-second host snapshot piggybacked on the daemon claim poll.</summary>
public sealed record HostTelemetrySample(
    DateTime Timestamp,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? SwapInBytesPerSecond,
    long? SwapOutBytesPerSecond,
    double? CpuStealPercent,
    double? IoWaitPercent,
    int CpuCores,
    int ActiveSlots);

public enum RunnerClaimStatus { Claimed, Empty, Invalid }

public sealed record RunnerClaimResponse(
    RunnerClaimStatus Status,
    string? TaskKey = null,
    string? JobId = null,
    string? ProjectName = null,
    RunLeaseInfoDto? Lease = null,
    string? Message = null,
    string? ProjectId = null,
    string? RepositoryUrl = null,
    string? DefaultBranch = null);

/// <summary>Runner -> Server: fenced normal completion after the remote CLI exits.</summary>
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
    string? Repository = null);

public sealed record RemoteRunCompletionResponse(
    string TaskKey,
    string Outcome,
    string TargetState,
    string? Message = null);

/// <summary>One consolidated output line, shaped to the server's CliOutputLine JSON.</summary>
public sealed record CliOutputLine(DateTime Timestamp, string Stream, string Text);

/// <summary>Runner -> Server: append output lines to the task's durable cli-output.log (/api/runner/logs).</summary>
public sealed record LogIngestRequest(string TaskKey, List<CliOutputLine> Lines);

public sealed record LogIngestResponse(string TaskKey, int Appended, string? Message = null);

/// <summary>One evidence file uploaded into the task's results/ folder.</summary>
public sealed record RunnerArtifactUpload(string Path, string ContentBase64);

/// <summary>Runner -> Server: upload base64 result files (/api/runner/artifacts).</summary>
public sealed record ArtifactIngestRequest(string TaskKey, List<RunnerArtifactUpload> Artifacts);

public sealed record ArtifactIngestResponse(
    string TaskKey,
    int Uploaded,
    List<string> Files,
    string? Message = null,
    string? CommitSha = null,
    string? CommitStatus = null);

/// <summary>One delivered artifact recorded by the external-completion endpoint.</summary>
public sealed record ExternalDeliverable(string? Path = null, string? Url = null, string? Note = null);

/// <summary>
/// Runner -> Server: reconcile the task the runner finished out-of-band
/// (/api/tasks/{jobId}/external-completion). This is how the remote run's result
/// re-enters the local board: the server writes status.md + deliverables,
/// terminalises the lifecycle, and moves the lane.
/// </summary>
public sealed record ExternalCompletionRequest(
    string? Summary,
    List<ExternalDeliverable>? Deliverables = null,
    string? Source = null,
    string? TargetState = null,
    List<string>? GateItems = null);

public sealed record ExternalCompletionResponse(
    string? JobId = null,
    string? TargetState = null,
    string? Source = null,
    string? EvidenceCommitSha = null);
