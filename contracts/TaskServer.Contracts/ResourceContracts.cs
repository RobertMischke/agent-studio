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
    DateTime? FinishedAt);

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
    DateTime? OccurredAt = null);

public sealed record EventDto(
    long Cursor,
    string EventId,
    string RunId,
    string TaskId,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    long Fence,
    DateTime OccurredAt);

public sealed record ArtifactIngestRequest(
    string ArtifactId,
    string Name,
    string MediaType,
    string ContentBase64,
    string Sha256,
    string IdempotencyKey,
    long Fence);

public sealed record ArtifactDto(
    string ArtifactId,
    string RunId,
    string Name,
    string MediaType,
    string Sha256,
    long SizeBytes,
    string IdempotencyKey,
    long Fence,
    DateTime CreatedAt);

public sealed record AuditRecordDto(
    long Sequence,
    DateTime OccurredAt,
    string ActorId,
    string Action,
    string TargetType,
    string TargetId,
    string DetailJson);
