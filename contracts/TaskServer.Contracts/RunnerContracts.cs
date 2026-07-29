namespace AgentStudio.TaskServer.Contracts;

public sealed record RegisterRunnerRequest(
    string Name,
    string HostId,
    string InstanceId,
    string RunnerVersion,
    int ProtocolVersion,
    IReadOnlyList<string>? Capabilities = null,
    string? HostOrchestratorMinimum = null,
    string? HostOrchestratorMaximum = null,
    int BootstrapMaxParallelism = 2);

/// <summary>
/// Server-owned runtime admission policy for one execution host. Projects use
/// this shared host policy and do not create independent capacity ceilings.
/// </summary>
public sealed record RuntimeCapacitySettingsDto(
    string HostId,
    int MaxParallelism,
    int TargetLoadPercent,
    string RampStrategy,
    long Version,
    DateTime UpdatedAt);

public sealed record UpdateRuntimeCapacitySettingsRequest(
    int MaxParallelism,
    int TargetLoadPercent,
    string RampStrategy,
    long ExpectedVersion);

public sealed record RunnerDto(
    string RunnerId,
    string Name,
    string HostId,
    string InstanceId,
    string RunnerVersion,
    int ProtocolVersion,
    string Status,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    RuntimeCapacitySettingsDto? RuntimeCapacity = null);

public sealed record ClaimRequest(
    string RunnerId,
    string InstanceId,
    int RequestedTtlSeconds = 120,
    int AvailableSlots = 1,
    RunnerProcessInventory? Inventory = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    int? EffectiveMaxParallelism = null);

public sealed record ClaimResponse(
    string Status,
    RunDto? Run = null,
    TaskDto? Task = null,
    LeaseDto? Lease = null,
    string? Message = null,
    IReadOnlyList<RunnerReconciliationAction>? ReconciliationActions = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? CanaryCapabilities = null,
    RuntimeCapacitySettingsDto? RuntimeCapacity = null);

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
    int RequestedTtlSeconds = 120,
    RunnerProcessInventory? Inventory = null);

public sealed record LeaseReleaseRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Outcome);

public sealed record LeaseResponse(
    string Status,
    LeaseDto? Lease = null,
    string? Message = null,
    IReadOnlyList<RunnerReconciliationAction>? ReconciliationActions = null);

/// <summary>
/// Comparable process truth reported by a runner on every claim poll and lease
/// heartbeat. A preparing run uses pid 0 until its CLI child has started.
/// </summary>
public sealed record RunnerProcessInventory(
    DateTime ObservedAt,
    IReadOnlyList<RunnerProcessInfo> Processes,
    IReadOnlyList<RunnerInvariantReport>? Reports = null,
    IReadOnlyList<string>? AcknowledgedActionIds = null);

public sealed record RunnerProcessInfo(
    string RunId,
    string TaskKey,
    int Pid,
    string Cwd,
    DateTime StartedAt);

/// <summary>
/// Runner-side invariant observation, including self-healing performed before
/// the report was sent, such as terminating a process whose cwd was deleted.
/// </summary>
public sealed record RunnerInvariantReport(
    string ReportId,
    string Category,
    DateTime DetectedAt,
    string Action,
    string Detail,
    string? RunId = null,
    string? TaskKey = null,
    int? Pid = null);

/// <summary>
/// Idempotent server reconciliation directive. The runner acknowledges the
/// action id in its next inventory after applying the action.
/// </summary>
public sealed record RunnerReconciliationAction(
    string ActionId,
    string Category,
    string Action,
    string Detail,
    int? Pid = null,
    string? RunId = null,
    string? TaskKey = null);

public sealed record CompleteRunRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string Outcome,
    string? Summary = null,
    string? ResultEnvelopeDigest = null,
    string? IdempotencyKey = null,
    long? Sequence = null,
    ExecutionOutcomeDecision? OutcomeDecision = null);
