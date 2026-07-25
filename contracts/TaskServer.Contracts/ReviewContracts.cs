namespace AgentStudio.TaskServer.Contracts;

public static class ReviewCapabilities
{
    public const string CodingExecutor = "coding-executor";
    public const string ReviewExecutor = "review-executor";
    public const string GitMaterialization = "review:git";
    public const string SourceBundleMaterialization = "review:source-bundle";
    public const string SemanticReview = "review:semantic";
    public const string VisionReview = "review:vision";
}

public sealed record ReviewCommandDto(
    string StepId,
    string Aspect,
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Required = true,
    int TimeoutSeconds = 1800);

public sealed record ReviewPlanDto(
    IReadOnlyList<ReviewCommandDto> Commands,
    IReadOnlyList<string> RequiredAspects,
    bool RequiresVisualReview = false,
    bool RequireDifferentHostFailureDomain = false);

public sealed record CreateReviewSubjectRequest(
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedResultSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string? CodingHostId,
    string ReviewPolicyHash,
    ReviewPlanDto Plan,
    string IdempotencyKey);

public sealed record ReviewSubjectDto(
    string SubjectId,
    string TaskId,
    string SourceRunId,
    string RepositoryId,
    string? RepositoryUrl,
    string ExpectedResultSha,
    string? ResultRef,
    string? SourceBundleArtifactId,
    string? SourceBundleSha256,
    string? CodingHostId,
    string ReviewPolicyHash,
    ReviewPlanDto Plan,
    DateTime CreatedAt);

public sealed record ReviewAttemptDto(
    string AttemptId,
    string SubjectId,
    string TaskId,
    int AttemptNumber,
    string Status,
    string? ExecutorId,
    string? HostId,
    long Fence,
    DateTime CreatedAt,
    DateTime? ReportedAt,
    DateTime? CleanedAt,
    string? Outcome,
    string? FailureClassification);

public sealed record ReviewLeaseDto(
    string LeaseId,
    string AttemptId,
    string SubjectId,
    string ExecutorId,
    string InstanceId,
    string HostId,
    long Fence,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string Status,
    string ResourceNamespace,
    int PortBase,
    long AuthorityEpoch = 0);

public sealed record ReviewClaimRequest(
    string ExecutorId,
    string InstanceId,
    int RequestedTtlSeconds = 120,
    int AvailableSlots = 1,
    IReadOnlyList<string>? RequiredCapabilities = null);

public sealed record ReviewClaimResponse(
    string Status,
    ReviewAttemptDto? Attempt = null,
    ReviewSubjectDto? Subject = null,
    ReviewLeaseDto? Lease = null,
    string? Message = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? CanaryCapabilities = null);

public sealed record ReviewLeaseRenewRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    int RequestedTtlSeconds = 120,
    long AuthorityEpoch = 0);

public sealed record ReviewCommandEvidenceDto(
    string StepId,
    string Aspect,
    string FileName,
    IReadOnlyList<string> Arguments,
    string ExpectedResultSha,
    string HeadBefore,
    string TreeBefore,
    DateTime StartedAt,
    DateTime FinishedAt,
    int? ExitCode,
    string? Signal,
    string StdoutSha256,
    string StderrSha256);

public sealed record ReviewWorkspaceProofDto(
    string RepositoryId,
    string ExpectedResultSha,
    string ActualHead,
    string TreeHash,
    bool DirtyBefore,
    bool DirtyAfter,
    string WorkspaceIdentity,
    string ResourceNamespace);

public sealed record ReviewEnvironmentDto(
    string HostId,
    string ExecutorId,
    string InstanceId,
    string OsDescription,
    string Architecture,
    string RuntimeVersion,
    IReadOnlyDictionary<string, string> Toolchain,
    IReadOnlyDictionary<string, string> Isolation);

public sealed record ReviewVerdictDto(
    string Aspect,
    string Status,
    string Classification,
    string Summary);

public sealed record ReviewArtifactEvidenceDto(
    string Name,
    string MediaType,
    string Sha256,
    long SizeBytes);

public sealed record ReviewReportRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    string Outcome,
    string? FailureClassification,
    string? Summary,
    ReviewWorkspaceProofDto Workspace,
    ReviewEnvironmentDto Environment,
    IReadOnlyList<ReviewCommandEvidenceDto> Commands,
    IReadOnlyList<ReviewArtifactEvidenceDto> Artifacts,
    IReadOnlyList<ReviewVerdictDto> Verdicts,
    long AuthorityEpoch = 0);

public sealed record ReviewReportDto(
    string ReportId,
    string AttemptId,
    string SubjectId,
    string Outcome,
    string? FailureClassification,
    string? Summary,
    string ReportSha256,
    DateTime ReceivedAt,
    bool RetryScheduled,
    string TaskState);

public sealed record ReviewCleanupRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    bool WorkspaceRemoved,
    string? FailureClassification = null,
    long AuthorityEpoch = 0);

public sealed record ReviewCleanupResponse(
    string Status,
    string AttemptId,
    DateTime CleanedAt,
    bool RetryScheduled);
