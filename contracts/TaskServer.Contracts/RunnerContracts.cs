namespace AgentStudio.TaskServer.Contracts;

public sealed record RegisterRunnerRequest(
    string Name,
    string HostId,
    string InstanceId,
    string RunnerVersion,
    int ProtocolVersion,
    IReadOnlyList<string>? Capabilities = null);

public sealed record RunnerDto(
    string RunnerId,
    string Name,
    string HostId,
    string InstanceId,
    string RunnerVersion,
    int ProtocolVersion,
    string Status,
    DateTime RegisteredAt,
    DateTime LastSeenAt);

public sealed record ClaimRequest(
    string RunnerId,
    string InstanceId,
    int RequestedTtlSeconds = 120,
    int AvailableSlots = 1);

public sealed record ClaimResponse(
    string Status,
    RunDto? Run = null,
    TaskDto? Task = null,
    LeaseDto? Lease = null,
    string? Message = null);

public sealed record LeaseDto(
    string LeaseId,
    string RunId,
    string TaskId,
    string RunnerId,
    string InstanceId,
    long Fence,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string Status);

public sealed record LeaseRenewRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    int RequestedTtlSeconds = 120);

public sealed record LeaseReleaseRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Outcome);

public sealed record LeaseResponse(string Status, LeaseDto? Lease = null, string? Message = null);

public sealed record CompleteRunRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Outcome,
    string? Summary = null);
