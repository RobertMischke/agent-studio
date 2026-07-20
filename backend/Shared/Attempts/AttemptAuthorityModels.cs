namespace AgentStudio.Shared;

public enum AttemptLifecycleState
{
    Pending,
    Leased,
    Completed,
    Failed,
    Cancelled,
    Superseded,
}

public enum ReviewTerminalOutcome
{
    InfrastructureFailure,
    ProductFailure,
    Inconclusive,
    Pass,
    Cancellation,
    Superseded,
}

public sealed record AttemptLeaseDto(
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string ExecutorId,
    string HostId,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    DateTime LastHeartbeat,
    string? ExecutorDisplayName = null,
    string? BackendName = null,
    int ProcessId = 0);

public sealed record ReviewSubjectDto(
    string SubjectId,
    string RepositoryId,
    string ExpectedResultSha,
    string SourceRunAttemptId,
    string TaskRequirementsHash,
    string ReviewPolicyHash,
    IReadOnlyList<string> EvidenceDigestInputs,
    DateTime CreatedAt);

public sealed record RunAttemptDto(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string? SourceAttemptId,
    AttemptLifecycleState State,
    AttemptLeaseDto? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? ResultSha,
    string? TerminalOutcome,
    string? TerminalReason,
    IReadOnlyList<string> EvidenceDigests);

public sealed record ReviewAttemptDto(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string SourceRunAttemptId,
    string? SourceReviewAttemptId,
    ReviewSubjectDto Subject,
    AttemptLifecycleState State,
    AttemptLeaseDto? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    ReviewTerminalOutcome? Outcome,
    string? FailureClassification,
    string? TestedResultSha,
    string? TerminalReason);

public sealed record AttemptWriteReference(
    string AttemptId,
    long Fence,
    long AuthorityEpoch,
    string IdempotencyKey);

public enum AttemptWriteStatus
{
    Accepted,
    Duplicate,
    Invalid,
    NotFound,
    StaleFence,
    AuthorityEpochMismatch,
    LeaseExpired,
    Superseded,
    SubjectMismatch,
    InvalidState,
}

public sealed record AttemptWriteResult(
    AttemptWriteStatus Status,
    string AttemptId,
    string? Message = null,
    RunAttemptDto? RunAttempt = null,
    ReviewAttemptDto? ReviewAttempt = null)
{
    public bool Accepted => Status is AttemptWriteStatus.Accepted or AttemptWriteStatus.Duplicate;
}

public sealed record CreateReviewAttemptRequest(
    string TaskKey,
    string RepositoryId,
    string ExpectedResultSha,
    string SourceRunAttemptId,
    string TaskRequirementsHash,
    string ReviewPolicyHash,
    IReadOnlyList<string>? EvidenceDigestInputs,
    string IdempotencyKey,
    string? SourceReviewAttemptId = null);

public sealed record ClaimReviewAttemptRequest(
    string AttemptId,
    string ExecutorId,
    string HostId,
    string IdempotencyKey,
    int? RequestedTtlSeconds = null);

public sealed record RenewAttemptLeaseRequest(
    AttemptWriteReference Write,
    string ExecutorId,
    int? RequestedTtlSeconds = null);

public sealed record SettleReviewAttemptRequest(
    AttemptWriteReference Write,
    string MaterializedResultSha,
    ReviewTerminalOutcome Outcome,
    string? FailureClassification = null,
    string? Reason = null);

public sealed record AttemptAuthorityProjection(
    string TaskKey,
    long AuthorityEpoch,
    RunAttemptDto? CurrentRunAttempt,
    ReviewSubjectDto? CurrentReviewSubject,
    ReviewAttemptDto? CurrentReviewAttempt,
    IReadOnlyList<RunAttemptDto> RunAttempts,
    IReadOnlyList<ReviewAttemptDto> ReviewAttempts,
    bool LegacyTask);
