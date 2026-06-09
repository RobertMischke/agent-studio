namespace OrchestratorApi.Models;

/// <summary>
/// Wire contracts for the Runner -> Task Server integration lease. The lease is
/// scoped to one project plus integration branch and serializes mutations of
/// that branch across runner machines. It is separate from the task run lease:
/// several tasks may run in parallel, but only the current integration lease
/// holder may fold a task branch into the integration branch.
/// </summary>
public sealed record IntegrationLeaseAcquireRequest(
    string ProjectName,
    string IntegrationBranch,
    string TaskKey,
    string RunnerId,
    string Hostname,
    int Pid,
    string BackendName,
    int? RequestedTtlSeconds = null);

public sealed record IntegrationLeaseHeartbeatRequest(
    string ProjectName,
    string IntegrationBranch,
    string LeaseId,
    long FencingToken,
    string RunnerId,
    int? RequestedTtlSeconds = null);

public sealed record IntegrationLeaseReleaseRequest(
    string ProjectName,
    string IntegrationBranch,
    string LeaseId,
    long FencingToken,
    string RunnerId);

public sealed record IntegrationLeaseInfoDto(
    string ProjectName,
    string IntegrationBranch,
    string TaskKey,
    string RunnerId,
    string Hostname,
    int Pid,
    string BackendName,
    string LeaseId,
    long FencingToken,
    DateTime AcquiredAt,
    DateTime ExpiresAt);

public sealed record IntegrationLeaseResponse(
    string Outcome,
    bool Granted,
    IntegrationLeaseInfoDto? Lease,
    int QueuePosition = 0,
    string? Message = null);
