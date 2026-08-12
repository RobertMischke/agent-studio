namespace AgentStudio.TaskServer.Contracts;

public static class ReviewCapabilities
{
    public const string CodingExecutor = "coding-executor";
    public const string ReviewExecutor = "review-executor";
    public const string GitMaterialization = "review:git";
    public const string SourceBundleMaterialization = "review:source-bundle";
    public const string SemanticReview = "review:semantic";
    public const string VisionReview = "review:vision";
    public const string BaselineComparison = "review:baseline-comparison";
    public const string DependencyPreparation = "review:dependency-preparation";
}

public static class ReviewCommandKinds
{
    public const string Tool = "tool";
    public const string AgentAspect = "agent-aspect";

    public static bool IsAgent(string? value)
        => string.Equals(value, AgentAspect, StringComparison.OrdinalIgnoreCase);
}

public sealed record ReviewDependencyScopeDto(
    string WorkingSubdir,
    IReadOnlyList<string> Lockfiles);

public sealed record ReviewPreparationCommandDto(
    string StepId,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingSubdir = "",
    int TimeoutSeconds = 1800,
    IReadOnlyList<ReviewDependencyScopeDto>? DependencyScopes = null);

public sealed record ReviewCommandDto(
    string StepId,
    string Aspect,
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Required = true,
    int TimeoutSeconds = 1800,
    bool CompareToBaseline = false,
    string ExecutionKind = ReviewCommandKinds.Tool,
    string? Prompt = null,
    string? CliType = null,
    string? Model = null,
    string? ThinkingLevel = null);

public sealed record ReviewPlanDto(
    IReadOnlyList<ReviewCommandDto> Commands,
    IReadOnlyList<string> RequiredAspects,
    bool RequiresVisualReview = false,
    bool RequireDifferentHostFailureDomain = false,
    string? IntegrationRef = null,
    IReadOnlyList<ReviewPreparationCommandDto>? Preparation = null,
    IReadOnlyList<string>? PreserveGlobs = null);

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
    string StderrSha256,
    string? BaselineSha = null,
    IReadOnlyList<string>? NewFailures = null,
    IReadOnlyList<string>? PreExistingFailures = null,
    bool BaselineCacheHit = false,
    bool RetryPerformed = false,
    IReadOnlyList<string>? FlakyQuarantinedFailures = null,
    string Phase = "verification",
    string WorkspaceRole = "candidate",
    ReviewCommandBudgetEvidenceDto? Budget = null,
    bool DependencyCacheHit = false,
    IReadOnlyList<ReviewDependencyCacheEvidenceDto>? DependencyCache = null,
    string ExecutionKind = ReviewCommandKinds.Tool,
    string ExecutionLocation = "remote",
    string? ExecutorId = null,
    string? HostId = null,
    string? AttemptId = null,
    string? Model = null,
    string? ThinkingLevel = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheCreationTokens = 0);

public sealed record ReviewCommandBudgetEvidenceDto(
    string Name,
    long LimitMs,
    long ConsumedMs,
    bool Violated);

public sealed record ReviewDependencyCacheEvidenceDto(
    string Scope,
    string State,
    string Reason,
    string LockHash,
    IReadOnlyList<string> Lockfiles,
    bool InstallRan);

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
    long SizeBytes,
    string? ContentBase64 = null);

public static class ReviewToolchainFailurePolicy
{
    public static bool IsUnavailable(
        IReadOnlyList<ReviewCommandEvidenceDto> commands,
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts)
        => commands.Any(command =>
            command.Phase == "verification"
            && (command.ExitCode == 127
                || ArtifactsContainMissingAngularToolchain(artifacts, command)));

    private static bool ArtifactsContainMissingAngularToolchain(
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
        ReviewCommandEvidenceDto command)
        => artifacts.Any(artifact =>
        {
            if (artifact.ContentBase64 is null
                || (!string.Equals(artifact.Sha256, command.StdoutSha256, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(artifact.Sha256, command.StderrSha256, StringComparison.OrdinalIgnoreCase)))
                return false;
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(artifact.ContentBase64));
                return text.Replace('\\', '/').Contains(
                    "node_modules/@angular/cli/bin/ng.js",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
        });
}

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

/// <summary>One terminal review used to calculate queue throughput and latency.</summary>
public sealed record ReviewCompletionSampleDto(
    DateTime CompletedAt,
    double DurationSeconds);

/// <summary>
/// Global Remote Review queue health. Queue depth is the Auto Review lane;
/// waiting depth is the subset that still needs a review worker rather than a
/// downstream orchestration decision.
/// </summary>
public sealed record ReviewQueueTelemetryDto(
    DateTime ObservedAt,
    int QueueDepth,
    int WaitingDepth,
    int ActiveReviews,
    double DrainRatePerHour,
    int DrainWindowMinutes,
    double? MedianReviewDurationSeconds,
    int DurationWindowHours,
    int DurationSampleCount,
    DateTime? LastDrainAt,
    DateTime? OldestWaitingAt,
    bool Stagnant,
    int StagnationThresholdMinutes,
    int StagnantForMinutes);

/// <summary>Pure queue-health policy shared by both Task Server mounts.</summary>
public static class ReviewQueueTelemetryPolicy
{
    public static ReviewQueueTelemetryDto Evaluate(
        DateTime nowUtc,
        int queueDepth,
        int waitingDepth,
        int activeReviews,
        DateTime? oldestWaitingAt,
        DateTime? lastDrainAt,
        IEnumerable<ReviewCompletionSampleDto> completions,
        TimeSpan drainWindow,
        TimeSpan durationWindow,
        TimeSpan stagnationThreshold)
    {
        var now = nowUtc.ToUniversalTime();
        drainWindow = Positive(drainWindow, TimeSpan.FromHours(1));
        durationWindow = Positive(durationWindow, TimeSpan.FromHours(24));
        stagnationThreshold = Positive(stagnationThreshold, TimeSpan.FromMinutes(30));

        var valid = completions
            .Where(sample => sample.DurationSeconds >= 0
                             && double.IsFinite(sample.DurationSeconds)
                             && sample.CompletedAt.ToUniversalTime() <= now)
            .Select(sample => sample with { CompletedAt = sample.CompletedAt.ToUniversalTime() })
            .ToList();
        var drainCount = valid.Count(sample => sample.CompletedAt >= now - drainWindow);
        var durationSamples = valid
            .Where(sample => sample.CompletedAt >= now - durationWindow)
            .Select(sample => sample.DurationSeconds)
            .Order()
            .ToArray();
        var median = Median(durationSamples);

        var oldest = NormalizePast(oldestWaitingAt, now);
        var lastDrain = NormalizePast(lastDrainAt, now);
        var progressAnchor = oldest;
        if (lastDrain is { } drain && (progressAnchor is null || drain > progressAnchor))
            progressAnchor = drain;
        var stagnantFor = waitingDepth > 0 && progressAnchor is { } anchor
            ? now - anchor
            : TimeSpan.Zero;
        var stagnant = waitingDepth > 0
                       && progressAnchor is not null
                       && stagnantFor >= stagnationThreshold;

        return new ReviewQueueTelemetryDto(
            now,
            Math.Max(0, queueDepth),
            Math.Max(0, waitingDepth),
            Math.Max(0, activeReviews),
            Math.Round(drainCount / drainWindow.TotalHours, 2),
            Math.Max(1, (int)Math.Round(drainWindow.TotalMinutes)),
            median is null ? null : Math.Round(median.Value, 2),
            Math.Max(1, (int)Math.Round(durationWindow.TotalHours)),
            durationSamples.Length,
            lastDrain,
            oldest,
            stagnant,
            Math.Max(1, (int)Math.Round(stagnationThreshold.TotalMinutes)),
            stagnant ? Math.Max(0, (int)Math.Floor(stagnantFor.TotalMinutes)) : 0);
    }

    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback)
        => value > TimeSpan.Zero ? value : fallback;

    private static DateTime? NormalizePast(DateTime? value, DateTime now)
        => value is { } present && present.ToUniversalTime() <= now
            ? present.ToUniversalTime()
            : null;

    private static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }
}
