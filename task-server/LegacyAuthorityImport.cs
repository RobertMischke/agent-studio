using System.Text.Json;

namespace AgentStudio.TaskServer;

internal sealed record LegacyAuthorityImport(
    long AuthorityEpoch,
    IReadOnlyDictionary<string, long> LastFenceByTask,
    IReadOnlyList<LegacyRunAuthority> Runs,
    IReadOnlyList<LegacyReviewAuthority> Reviews);

internal sealed record LegacyLeaseAuthority(
    string LeaseId,
    long Fence,
    long AuthorityEpoch,
    string ExecutorId,
    string HostId,
    DateTime AcquiredAt,
    DateTime ExpiresAt,
    string InstanceId);

internal sealed record LegacyRunAuthority(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    int State,
    LegacyLeaseAuthority? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? ResultSha,
    string? TerminalOutcome);

internal sealed record LegacyReviewAuthority(
    string AttemptId,
    string TaskKey,
    string RepositoryId,
    string SourceRunAttemptId,
    string SubjectId,
    string ExpectedResultSha,
    string ReviewPolicyHash,
    string? RepositoryUrl,
    string? ResultRef,
    JsonElement? Plan,
    int State,
    LegacyLeaseAuthority? Lease,
    long LastFence,
    long AuthorityEpoch,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    string? Outcome,
    string? FailureClassification,
    IReadOnlyList<LegacyReviewReportAuthority> Reports);

internal sealed record LegacyReviewReportAuthority(
    string IdempotencyKey,
    string PayloadJson,
    DateTime ReceivedAt);
