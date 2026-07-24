namespace AgentStudio.TaskServer.Contracts;

public sealed record WorkspaceDto(
    string WorkspaceId,
    string Name,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProjectDto(
    string ProjectId,
    string WorkspaceId,
    string Name,
    string TaskKeyPrefix,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record TaskDto(
    string TaskId,
    string ProjectId,
    string TaskKey,
    string Title,
    string State,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Body = null);

public sealed record RunDto(
    string RunId,
    string TaskId,
    string Status,
    string? RunnerId,
    long? Fence,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? ResultSha = null,
    string? RepositoryId = null);

public sealed record CreateWorkspaceRequest(string Name, string? WorkspaceId = null);
public sealed record CreateProjectRequest(string WorkspaceId, string Name, string TaskKeyPrefix, string? ProjectId = null);
public sealed record CreateTaskRequest(string Title, string? Body = null, string State = "0-backlog", string? TaskId = null, string? TaskKey = null);
public sealed record UpdateTaskRequest(string? Title, string? Body, string? State, long ExpectedVersion);

public sealed record EventIngestRequest(
    string EventId,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    long Fence,
    DateTime? OccurredAt = null,
    string? RunnerId = null,
    string? InstanceId = null,
    string? LeaseId = null,
    long? Sequence = null);

public sealed record EventDto(
    long Cursor,
    string EventId,
    string RunId,
    string TaskId,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    long Fence,
    DateTime OccurredAt,
    long? Sequence = null);

public sealed record ArtifactIngestRequest(
    string ArtifactId,
    string Name,
    string MediaType,
    string ContentBase64,
    string Sha256,
    string IdempotencyKey,
    long Fence,
    string? RunnerId = null,
    string? InstanceId = null,
    string? LeaseId = null,
    long? Sequence = null);

public sealed record ArtifactDto(
    string ArtifactId,
    string RunId,
    string Name,
    string MediaType,
    string Sha256,
    long SizeBytes,
    string IdempotencyKey,
    long Fence,
    DateTime CreatedAt,
    long? Sequence = null);

/// <summary>
/// One immutable identity needed to reconstruct a coding result. Git submodules
/// use the path and commit SHA. LFS objects use the repository path and object
/// id. Entries are sorted before the envelope digest is calculated.
/// </summary>
public sealed record ResultDependencyIdentity(string Path, string ObjectId);

/// <summary>
/// Immutable coding result handed from a Remote Coding Executor to the Task
/// Server. Exactly one of <see cref="ImmutableRemoteRef"/> and
/// <see cref="SourceBundleDigest"/> must be present.
/// </summary>
public sealed record ImmutableResultEnvelope(
    string RepositoryId,
    string SourceRunAttemptId,
    string BaseSha,
    string ResultSha,
    string? ImmutableRemoteRef,
    string? SourceBundleDigest,
    string ArtifactManifestDigest,
    IReadOnlyList<ResultDependencyIdentity>? Submodules = null,
    IReadOnlyList<ResultDependencyIdentity>? LfsObjects = null);

public sealed record ResultHandoffRequest(
    string RunnerId,
    string InstanceId,
    string LeaseId,
    long Fence,
    long Sequence,
    string IdempotencyKey,
    string EnvelopeDigest,
    ImmutableResultEnvelope Envelope);

public sealed record ResultHandoffAck(
    string RunId,
    long AcknowledgedSequence,
    string EnvelopeDigest,
    string State,
    DateTime AcknowledgedAt,
    DateTime RetainUntil,
    bool Replay);

public sealed record ResultHandoffDto(
    string RunId,
    ImmutableResultEnvelope Envelope,
    string EnvelopeDigest,
    long AcknowledgedSequence,
    DateTime AcknowledgedAt,
    DateTime RetainUntil);

public sealed record RunnerOutboxStatusRequest(
    string InstanceId,
    long LastSequence,
    long LastAcknowledgedSequence,
    int BacklogCount,
    long? OldestUnacknowledgedSequence,
    string FinalHandoffState,
    string? RunId,
    string? EnvelopeDigest,
    DateTime ObservedAt);

public sealed record RunnerOutboxStatusDto(
    string RunnerId,
    string InstanceId,
    long LastSequence,
    long LastAcknowledgedSequence,
    int BacklogCount,
    long? OldestUnacknowledgedSequence,
    string FinalHandoffState,
    string? RunId,
    string? EnvelopeDigest,
    DateTime ObservedAt);

public sealed record ArtifactContentDto(
    string ArtifactId,
    string RunId,
    string Name,
    string MediaType,
    string Sha256,
    string ContentBase64,
    long SizeBytes);

public sealed record AuditRecordDto(
    long Sequence,
    DateTime OccurredAt,
    string ActorId,
    string Action,
    string TargetType,
    string TargetId,
    string DetailJson);
