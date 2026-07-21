using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Durable Task Server authority for coding and review attempts. This store owns
/// identity, leases, monotonically increasing fences, the authority epoch,
/// immutable review subjects, terminal facts, and delivery idempotency. It does
/// not resolve or materialize a repository and never runs a product command.
/// </summary>
public sealed class AttemptAuthorityService
{
    public const string RelativePath = ".metadata/attempt-authority.json";
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger<AttemptAuthorityService> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly IAtomicJsonFileWriter _writer;
    private AuthorityState _state;

    public AttemptAuthorityService(
        IConfiguration configuration,
        ILogger<AttemptAuthorityService> logger,
        Func<DateTime>? utcNow = null,
        IAtomicJsonFileWriter? writer = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _writer = writer ?? new AtomicJsonFileWriter();
        var root = configuration["TaskRepository"];
        _path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, RelativePath);
        _state = Load();
        NormalizeLoadedState();
        if (_state.AuthorityEpoch <= 0) _state.AuthorityEpoch = 1;
    }

    internal AttemptAuthorityService(ILogger<AttemptAuthorityService> logger, Func<DateTime>? utcNow = null)
        : this(new ConfigurationBuilder().Build(), logger, utcNow)
    {
    }

    public long AuthorityEpoch
    {
        get { lock (_gate) return _state.AuthorityEpoch; }
    }

    public AttemptWriteResult AcquireRun(
        string taskKey,
        string repositoryId,
        string? sourceAttemptId,
        string executorId,
        string hostId,
        int? requestedTtlSeconds,
        string idempotencyKey,
        string? executorDisplayName = null,
        string? backendName = null,
        int processId = 0)
    {
        if (Blank(taskKey) || Blank(repositoryId) || Blank(executorId) || Blank(hostId) || Blank(idempotencyKey))
            return InvalidRun("TaskKey, RepositoryId, ExecutorId, HostId, and IdempotencyKey are required.");

        lock (_gate)
        {
            var deliveryKey = DeliveryKey("acquire", idempotencyKey);
            var duplicate = FindIdempotentRun(taskKey, deliveryKey);
            if (duplicate is not null)
            {
                var replayedAt = _utcNow();
                if (!IsCurrentRun(duplicate) || duplicate.State == AttemptLifecycleState.Superseded)
                    return new AttemptWriteResult(AttemptWriteStatus.Superseded, duplicate.AttemptId, RunAttempt: ToDto(duplicate));
                if (duplicate.AuthorityEpoch != _state.AuthorityEpoch)
                    return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, duplicate.AttemptId, RunAttempt: ToDto(duplicate));
                if (duplicate.State != AttemptLifecycleState.Leased)
                    return new AttemptWriteResult(AttemptWriteStatus.InvalidState, duplicate.AttemptId,
                        $"RunAttempt is {duplicate.State} and cannot be reacquired by replaying an old delivery.",
                        RunAttempt: ToDto(duplicate));
                if (duplicate.Lease is null || duplicate.Lease.ExpiresAt <= replayedAt)
                    return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, duplicate.AttemptId, RunAttempt: ToDto(duplicate));
                if (!Same(duplicate.Lease.ExecutorId, executorId))
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, duplicate.AttemptId,
                        "The acquire delivery belongs to another executor.", RunAttempt: ToDto(duplicate));
                return new AttemptWriteResult(AttemptWriteStatus.Duplicate, duplicate.AttemptId, RunAttempt: ToDto(duplicate));
            }

            var now = _utcNow();
            var current = CurrentRun(taskKey);
            if (current is { State: AttemptLifecycleState.Leased, Lease: not null }
                && current.Lease.ExpiresAt > now
                && current.AuthorityEpoch == _state.AuthorityEpoch)
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.InvalidState,
                    current.AttemptId,
                    $"Task '{Normalize(taskKey)}' is leased by '{current.Lease.ExecutorId}' until {current.Lease.ExpiresAt:o}.",
                    RunAttempt: ToDto(current));
            }

            if (current is not null && !Terminal(current.State))
            {
                current.State = AttemptLifecycleState.Superseded;
                current.TerminalAt = now;
                current.TerminalOutcome = "superseded";
                current.TerminalReason = current.AuthorityEpoch != _state.AuthorityEpoch
                    ? "authority epoch changed"
                    : "lease expired and a new executor took authority";
            }

            var fence = NextFenceLocked(taskKey);
            var attempt = new RunAttemptRecord
            {
                AttemptId = NewId("run"),
                TaskKey = Normalize(taskKey),
                RepositoryId = Normalize(repositoryId),
                SourceAttemptId = NormalizeNull(sourceAttemptId),
                State = AttemptLifecycleState.Leased,
                LastFence = fence,
                AuthorityEpoch = _state.AuthorityEpoch,
                CreatedAt = now,
                Lease = NewLease(executorId, hostId, fence, requestedTtlSeconds, now,
                    executorDisplayName, backendName, processId),
                IdempotencyKeys = [deliveryKey],
            };
            _state.RunAttempts.Add(attempt);
            _state.CurrentRunByTask[Normalize(taskKey)] = attempt.AttemptId;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, attempt.AttemptId, RunAttempt: ToDto(attempt));
        }
    }

    public AttemptWriteResult RenewRun(
        AttemptWriteReference write,
        string executorId,
        int? requestedTtlSeconds,
        string? leaseId = null)
    {
        lock (_gate)
        {
            var replayed = FindRun(write.AttemptId);
            if (replayed is not null
                && replayed.IdempotencyKeys.Contains(DeliveryKey("renew", write.IdempotencyKey)))
            {
                return ClassifyRunLeaseReplay(replayed, write, executorId, leaseId);
            }
            var validation = ValidateRunWriteLocked(
                write, executorId, recordIdempotency: false, idempotencyScope: "renew", leaseId: leaseId);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(write.AttemptId)!;
            var now = _utcNow();
            run.Lease!.ExpiresAt = now.Add(NormalizeTtl(requestedTtlSeconds));
            run.Lease.LastHeartbeat = now;
            run.IdempotencyKeys.Add(DeliveryKey("renew", write.IdempotencyKey));
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult ReleaseRun(
        AttemptWriteReference write,
        string executorId,
        string? leaseId = null)
    {
        lock (_gate)
        {
            var existing = FindRun(write.AttemptId);
            var deliveryKey = DeliveryKey("release", write.IdempotencyKey);
            if (existing is not null && existing.IdempotencyKeys.Contains(deliveryKey))
            {
                var duplicateStatus = IsCurrentRun(existing)
                    ? AttemptWriteStatus.Duplicate
                    : AttemptWriteStatus.Superseded;
                return new AttemptWriteResult(duplicateStatus, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            // Completion revokes authority through state, but the final cleanup
            // delivery is still acknowledged and the historical lease facts are
            // retained for audit and restart reconstruction.
            if (existing is not null && Terminal(existing.State))
            {
                if (write.AuthorityEpoch != _state.AuthorityEpoch || existing.AuthorityEpoch != _state.AuthorityEpoch)
                    return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, existing.AttemptId, RunAttempt: ToDto(existing));
                if (!IsCurrentRun(existing) || existing.State == AttemptLifecycleState.Superseded)
                    return new AttemptWriteResult(AttemptWriteStatus.Superseded, existing.AttemptId, RunAttempt: ToDto(existing));
                if (write.Fence != existing.LastFence)
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId, RunAttempt: ToDto(existing));
                if (existing.Lease is null || !Same(existing.Lease.ExecutorId, executorId))
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId,
                        "Executor does not own this attempt's terminal cleanup.", RunAttempt: ToDto(existing));
                if (!Blank(leaseId) && !Same(existing.Lease.LeaseId, leaseId))
                    return new AttemptWriteResult(AttemptWriteStatus.StaleFence, existing.AttemptId,
                        "Lease ID does not own this attempt's terminal cleanup.", RunAttempt: ToDto(existing));

                existing.IdempotencyKeys.Add(deliveryKey);
                PersistLocked();
                return new AttemptWriteResult(AttemptWriteStatus.Accepted, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            var validation = ValidateRunWriteLocked(
                write, executorId, recordIdempotency: false, idempotencyScope: "release", leaseId: leaseId);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(write.AttemptId)!;
            run.IdempotencyKeys.Add(deliveryKey);
            run.Lease = null;
            if (!Terminal(run.State)) run.State = AttemptLifecycleState.Pending;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult AcceptRunWrite(AttemptWriteReference write)
        => ExecuteRunWrite(write, "write", expectedTaskKey: null, static () => { });

    public AttemptWriteResult RecordEvidenceDigest(AttemptWriteReference write, string digest)
    {
        if (Blank(digest)) return InvalidRun("Evidence digest is required.");
        return ExecuteRunWrite(
            write, "evidence", expectedTaskKey: null, static () => { }, digest);
    }

    /// <summary>
    /// Validates one fenced delivery and performs its Task Server side effect
    /// inside the same authority critical section. The idempotency key is only
    /// persisted after the side effect succeeds, so an I/O failure can retry
    /// the same delivery instead of being mistaken for a completed duplicate.
    /// </summary>
    public AttemptWriteResult ExecuteRunWrite(
        AttemptWriteReference write,
        string operation,
        string? expectedTaskKey,
        Action sideEffect,
        string? evidenceDigest = null)
    {
        if (Blank(operation)) return InvalidRun("Write operation is required.");
        ArgumentNullException.ThrowIfNull(sideEffect);

        lock (_gate)
        {
            var result = ValidateRunWriteLocked(
                write,
                null,
                recordIdempotency: false,
                idempotencyScope: operation,
                expectedTaskKey: expectedTaskKey);
            if (result.Status != AttemptWriteStatus.Accepted) return result;

            sideEffect();
            var run = FindRun(write.AttemptId)!;
            run.IdempotencyKeys.Add(DeliveryKey(operation, write.IdempotencyKey));
            if (!Blank(evidenceDigest)
                && !run.EvidenceDigests.Contains(evidenceDigest!, StringComparer.Ordinal))
            {
                run.EvidenceDigests.Add(evidenceDigest!);
            }
            PersistLocked();
            return new AttemptWriteResult(
                AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult SettleRun(
        AttemptWriteReference write,
        string outcome,
        string? resultSha,
        string? reason,
        string? executorId = null,
        string? leaseId = null,
        string? expectedTaskKey = null)
    {
        lock (_gate)
        {
            var existing = FindRun(write.AttemptId);
            var deliveryKey = DeliveryKey("settle", write.IdempotencyKey);
            if (existing is not null && existing.IdempotencyKeys.Contains(deliveryKey))
            {
                var duplicateStatus = IsCurrentRun(existing)
                    ? AttemptWriteStatus.Duplicate
                    : AttemptWriteStatus.Superseded;
                return new AttemptWriteResult(duplicateStatus, existing.AttemptId, RunAttempt: ToDto(existing));
            }

            var validation = ValidateRunWriteLocked(
                write,
                executorId,
                recordIdempotency: false,
                idempotencyScope: "settle",
                leaseId: leaseId,
                expectedTaskKey: expectedTaskKey);
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            var run = FindRun(write.AttemptId)!;
            var normalizedOutcome = Normalize(outcome).ToLowerInvariant();
            if (normalizedOutcome is "done" or "noop" && Blank(resultSha))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.Invalid,
                    run.AttemptId,
                    "Successful remote completion requires the immutable Result-SHA.",
                    RunAttempt: ToDto(run));
            }

            run.IdempotencyKeys.Add(deliveryKey);
            run.State = normalizedOutcome is "done" or "noop"
                ? AttemptLifecycleState.Completed
                : normalizedOutcome is "cancelled" ? AttemptLifecycleState.Cancelled : AttemptLifecycleState.Failed;
            run.TerminalAt = _utcNow();
            run.TerminalOutcome = normalizedOutcome;
            run.TerminalReason = NormalizeNull(reason);
            run.ResultSha = NormalizeNull(resultSha)?.ToLowerInvariant();
            if (run.State == AttemptLifecycleState.Completed
                && CurrentReview(run.TaskKey) is { } olderReview
                && !Terminal(olderReview.State)
                && !Same(olderReview.SourceRunAttemptId, run.AttemptId))
            {
                olderReview.State = AttemptLifecycleState.Superseded;
                olderReview.Outcome = ReviewTerminalOutcome.Superseded;
                olderReview.TerminalAt = run.TerminalAt;
                olderReview.TerminalReason = $"RunAttempt {run.AttemptId} produced a newer immutable result.";
            }
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
        }
    }

    public AttemptWriteResult CreateReviewAttempt(CreateReviewAttemptRequest request)
    {
        if (Blank(request.TaskKey) || Blank(request.RepositoryId) || Blank(request.ExpectedResultSha)
            || Blank(request.SourceRunAttemptId) || Blank(request.TaskRequirementsHash)
            || Blank(request.ReviewPolicyHash) || Blank(request.IdempotencyKey))
            return new AttemptWriteResult(AttemptWriteStatus.Invalid, string.Empty, "Complete immutable ReviewSubject identity and IdempotencyKey are required.");

        lock (_gate)
        {
            var deliveryKey = DeliveryKey("create", request.IdempotencyKey);
            var duplicate = FindIdempotentReview(request.TaskKey, deliveryKey);
            if (duplicate is not null)
            {
                if (!IsCurrentReview(duplicate) || duplicate.State == AttemptLifecycleState.Superseded)
                    return new AttemptWriteResult(AttemptWriteStatus.Superseded, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
                if (duplicate.AuthorityEpoch != _state.AuthorityEpoch)
                    return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
                return new AttemptWriteResult(AttemptWriteStatus.Duplicate, duplicate.AttemptId, ReviewAttempt: ToDto(duplicate));
            }

            var run = FindRun(request.SourceRunAttemptId);
            var expectedSha = Normalize(request.ExpectedResultSha).ToLowerInvariant();
            if (run is null || run.State != AttemptLifecycleState.Completed
                || !Same(run.TaskKey, request.TaskKey)
                || !Same(run.RepositoryId, request.RepositoryId)
                || !Same(run.ResultSha, expectedSha))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.SubjectMismatch,
                    string.Empty,
                    "ReviewAttempt source must be the completed RunAttempt for the same task, repository, and exact Result-SHA.");
            }
            if (!IsCurrentRun(run))
            {
                return new AttemptWriteResult(
                    AttemptWriteStatus.Superseded,
                    run.AttemptId,
                    "ReviewAttempt source is no longer the current RunAttempt for this task.",
                    RunAttempt: ToDto(run));
            }

            ReviewAttemptRecord? sourceReview = null;
            if (!Blank(request.SourceReviewAttemptId))
            {
                sourceReview = FindReview(request.SourceReviewAttemptId!);
                if (sourceReview is null
                    || sourceReview.Outcome is not (ReviewTerminalOutcome.InfrastructureFailure or ReviewTerminalOutcome.Inconclusive or ReviewTerminalOutcome.Cancellation)
                    || !Same(sourceReview.Subject.ExpectedResultSha, expectedSha))
                {
                    return new AttemptWriteResult(AttemptWriteStatus.InvalidState, string.Empty,
                        "A review retry must link a terminal infrastructure, inconclusive, or cancelled ReviewAttempt for the same subject.");
                }
            }

            var now = _utcNow();
            var evidence = (request.EvidenceDigestInputs ?? [])
                .Select(Normalize)
                .Where(x => x.Length > 0)
                .Order(StringComparer.Ordinal)
                .ToList();
            var subjectId = SubjectId(request.RepositoryId, expectedSha, request.SourceRunAttemptId,
                request.TaskRequirementsHash, request.ReviewPolicyHash, evidence);
            if (sourceReview is not null && !Same(sourceReview.Subject.SubjectId, subjectId))
            {
                return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, sourceReview.AttemptId,
                    "A review infrastructure retry must retain the exact immutable ReviewSubject.",
                    ReviewAttempt: ToDto(sourceReview));
            }
            var subject = sourceReview?.Subject ?? new ReviewSubjectRecord
            {
                SubjectId = subjectId,
                RepositoryId = Normalize(request.RepositoryId),
                ExpectedResultSha = expectedSha,
                SourceRunAttemptId = run.AttemptId,
                TaskRequirementsHash = Normalize(request.TaskRequirementsHash),
                ReviewPolicyHash = Normalize(request.ReviewPolicyHash),
                EvidenceDigestInputs = evidence,
                CreatedAt = now,
            };

            var current = CurrentReview(request.TaskKey);
            if (current is not null && !Terminal(current.State))
            {
                current.State = AttemptLifecycleState.Superseded;
                current.Outcome = ReviewTerminalOutcome.Superseded;
                current.TerminalAt = now;
                current.TerminalReason = Same(current.Subject.SubjectId, subjectId)
                    ? "replaced by review retry"
                    : "a newer immutable result became current";
            }

            var attempt = new ReviewAttemptRecord
            {
                AttemptId = NewId("review"),
                TaskKey = Normalize(request.TaskKey),
                RepositoryId = Normalize(request.RepositoryId),
                SourceRunAttemptId = run.AttemptId,
                SourceReviewAttemptId = sourceReview?.AttemptId,
                Subject = subject,
                State = AttemptLifecycleState.Pending,
                AuthorityEpoch = _state.AuthorityEpoch,
                CreatedAt = now,
                IdempotencyKeys = [deliveryKey],
            };
            _state.ReviewAttempts.Add(attempt);
            _state.CurrentReviewByTask[attempt.TaskKey] = attempt.AttemptId;
            _state.CurrentSubjectByTask[attempt.TaskKey] = subject;
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, attempt.AttemptId, ReviewAttempt: ToDto(attempt));
        }
    }

    public AttemptWriteResult RenewReview(AttemptWriteReference write, string executorId, int? requestedTtlSeconds)
    {
        lock (_gate)
        {
            var review = FindReview(write.AttemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
            var deliveryKey = DeliveryKey("renew", write.IdempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
                return ClassifyReviewLeaseReplay(review, executorId, write: write);
            var validation = ValidateReviewWriteLocked(write, "renew");
            if (validation.Status != AttemptWriteStatus.Accepted) return validation;
            if (!Same(review.Lease!.ExecutorId, executorId))
                return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId,
                    "Executor does not own this ReviewAttempt lease.", ReviewAttempt: ToDto(review));

            var now = _utcNow();
            review.Lease.ExpiresAt = now.Add(NormalizeTtl(requestedTtlSeconds));
            review.Lease.LastHeartbeat = now;
            review.IdempotencyKeys.Add(deliveryKey);
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    public AttemptWriteResult ClaimReview(string attemptId, string executorId, string hostId, int? requestedTtlSeconds, string idempotencyKey)
    {
        if (Blank(attemptId) || Blank(executorId) || Blank(hostId) || Blank(idempotencyKey))
            return new AttemptWriteResult(
                AttemptWriteStatus.Invalid,
                Normalize(attemptId),
                "AttemptId, ExecutorId, HostId, and IdempotencyKey are required.");

        lock (_gate)
        {
            var review = FindReview(attemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, Normalize(attemptId));
            var deliveryKey = DeliveryKey("claim", idempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
                return ClassifyReviewLeaseReplay(review, executorId, claimDeliveryKey: deliveryKey);
            if (!IsCurrentReview(review) || Terminal(review.State))
                return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
            var now = _utcNow();
            if (review.Lease is { } live && live.ExpiresAt > now && review.AuthorityEpoch == _state.AuthorityEpoch)
                return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId, $"ReviewAttempt is leased by '{live.ExecutorId}'.", ReviewAttempt: ToDto(review));

            var fence = NextFenceLocked(review.TaskKey);
            review.LastFence = fence;
            review.AuthorityEpoch = _state.AuthorityEpoch;
            review.State = AttemptLifecycleState.Leased;
            review.Lease = NewLease(executorId, hostId, fence, requestedTtlSeconds, now);
            review.CurrentClaimDeliveryKey = deliveryKey;
            review.IdempotencyKeys.Add(deliveryKey);
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    public AttemptWriteResult SettleReview(SettleReviewAttemptRequest request)
    {
        lock (_gate)
        {
            var review = FindReview(request.Write.AttemptId);
            if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, request.Write.AttemptId);
            var deliveryKey = DeliveryKey("settle", request.Write.IdempotencyKey);
            if (review.IdempotencyKeys.Contains(deliveryKey))
            {
                var replayStatus = request.Write.AuthorityEpoch != _state.AuthorityEpoch
                                   || review.AuthorityEpoch != _state.AuthorityEpoch
                    ? AttemptWriteStatus.AuthorityEpochMismatch
                    : !IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded
                        ? AttemptWriteStatus.Superseded
                        : request.Write.Fence != review.LastFence
                            ? AttemptWriteStatus.StaleFence
                            : AttemptWriteStatus.Duplicate;
                return new AttemptWriteResult(replayStatus, review.AttemptId, ReviewAttempt: ToDto(review));
            }
            var validation = ValidateReviewWriteLocked(request.Write, "settle");
            if (validation.Status != AttemptWriteStatus.Accepted)
            {
                if (validation.Status == AttemptWriteStatus.Superseded)
                {
                    review.IdempotencyKeys.Add(deliveryKey);
                    review.Reports.Add(ToReport(request, validation.Status, _utcNow()));
                    PersistLocked();
                    return validation with { ReviewAttempt = ToDto(review) };
                }
                return validation;
            }

            review.IdempotencyKeys.Add(deliveryKey);
            review.TestedResultSha = Normalize(request.MaterializedResultSha).ToLowerInvariant();
            review.TerminalAt = _utcNow();
            if (!Same(review.TestedResultSha, review.Subject.ExpectedResultSha))
            {
                review.Reports.Add(ToReport(request, AttemptWriteStatus.SubjectMismatch, review.TerminalAt.Value));
                review.State = AttemptLifecycleState.Failed;
                review.Outcome = ReviewTerminalOutcome.InfrastructureFailure;
                review.FailureClassification = "immutable-result-mismatch";
                review.TerminalReason = $"Expected {review.Subject.ExpectedResultSha}, materialized {review.TestedResultSha}.";
                PersistLocked();
                return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, review.AttemptId,
                    review.TerminalReason, ReviewAttempt: ToDto(review));
            }

            review.Reports.Add(ToReport(request, AttemptWriteStatus.Accepted, review.TerminalAt.Value));
            review.Outcome = request.Outcome;
            review.FailureClassification = NormalizeNull(request.FailureClassification);
            review.TerminalReason = NormalizeNull(request.Reason);
            review.State = request.Outcome switch
            {
                ReviewTerminalOutcome.Pass => AttemptLifecycleState.Completed,
                ReviewTerminalOutcome.Cancellation => AttemptLifecycleState.Cancelled,
                ReviewTerminalOutcome.Superseded => AttemptLifecycleState.Superseded,
                _ => AttemptLifecycleState.Failed,
            };
            PersistLocked();
            return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
        }
    }

    public long RotateAuthorityEpoch(string reason)
    {
        lock (_gate)
        {
            _state.AuthorityEpoch++;
            var now = _utcNow();
            foreach (var run in _state.RunAttempts.Where(x => !Terminal(x.State)))
            {
                run.State = AttemptLifecycleState.Superseded;
                run.TerminalAt = now;
                run.TerminalOutcome = "superseded";
                run.TerminalReason = $"Authority epoch changed: {reason}";
            }
            foreach (var review in _state.ReviewAttempts.Where(x => !Terminal(x.State)))
            {
                review.State = AttemptLifecycleState.Superseded;
                review.Outcome = ReviewTerminalOutcome.Superseded;
                review.TerminalAt = now;
                review.TerminalReason = $"Authority epoch changed: {reason}";
            }
            PersistLocked();
            _logger.LogWarning("attempt-authority-epoch-rotated epoch={Epoch} reason={Reason}", _state.AuthorityEpoch, reason);
            return _state.AuthorityEpoch;
        }
    }

    public RunAttemptDto? GetRun(string attemptId)
    {
        lock (_gate) return FindRun(attemptId) is { } run ? ToDto(run) : null;
    }

    public ReviewAttemptDto? GetReview(string attemptId)
    {
        lock (_gate) return FindReview(attemptId) is { } review ? ToDto(review) : null;
    }

    public AttemptAuthorityProjection GetTaskProjection(string taskKey)
    {
        lock (_gate)
        {
            var key = Normalize(taskKey);
            var runs = _state.RunAttempts.Where(x => Same(x.TaskKey, key)).Select(ToDto).ToList();
            var reviews = _state.ReviewAttempts.Where(x => Same(x.TaskKey, key)).Select(ToDto).ToList();
            var currentRun = CurrentRun(key);
            var currentReview = CurrentReview(key);
            _state.CurrentSubjectByTask.TryGetValue(key, out var subject);
            return new AttemptAuthorityProjection(
                key,
                _state.AuthorityEpoch,
                currentRun is null ? null : ToDto(currentRun),
                subject is null ? null : ToDto(subject),
                currentReview is null ? null : ToDto(currentReview),
                runs,
                reviews,
                runs.Count == 0 && reviews.Count == 0);
        }
    }

    private AttemptWriteResult ValidateRunWriteLocked(
        AttemptWriteReference write,
        string? executorId,
        bool recordIdempotency,
        string idempotencyScope,
        string? leaseId = null,
        string? expectedTaskKey = null)
    {
        if (Blank(write.AttemptId) || write.Fence <= 0 || write.AuthorityEpoch <= 0 || Blank(write.IdempotencyKey))
            return InvalidRun("AttemptId, Fence, AuthorityEpoch, and IdempotencyKey are required.");
        var run = FindRun(write.AttemptId);
        if (run is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
        var deliveryKey = DeliveryKey(idempotencyScope, write.IdempotencyKey);
        if (run.IdempotencyKeys.Contains(deliveryKey))
            return new AttemptWriteResult(AttemptWriteStatus.Duplicate, run.AttemptId, RunAttempt: ToDto(run));
        if (write.AuthorityEpoch != _state.AuthorityEpoch || run.AuthorityEpoch != _state.AuthorityEpoch)
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, run.AttemptId, RunAttempt: ToDto(run));
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, run.AttemptId, RunAttempt: ToDto(run));
        if (write.Fence != run.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        if (run.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, run.AttemptId,
                $"RunAttempt is {run.State} and no longer has write authority.", RunAttempt: ToDto(run));
        if (run.Lease is null || run.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, run.AttemptId, RunAttempt: ToDto(run));
        if (!Blank(executorId) && !Same(run.Lease.ExecutorId, executorId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, "Executor does not own this lease.", RunAttempt: ToDto(run));
        if (!Blank(leaseId) && !Same(run.Lease.LeaseId, leaseId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, "Lease ID does not own this attempt.", RunAttempt: ToDto(run));
        if (!Blank(expectedTaskKey) && !Same(run.TaskKey, expectedTaskKey))
            return new AttemptWriteResult(AttemptWriteStatus.SubjectMismatch, run.AttemptId, "RunAttempt does not belong to the requested task.", RunAttempt: ToDto(run));
        if (recordIdempotency) run.IdempotencyKeys.Add(deliveryKey);
        return new AttemptWriteResult(AttemptWriteStatus.Accepted, run.AttemptId, RunAttempt: ToDto(run));
    }

    private AttemptWriteResult ClassifyRunLeaseReplay(
        RunAttemptRecord run,
        AttemptWriteReference write,
        string executorId,
        string? leaseId)
    {
        if (write.AuthorityEpoch != _state.AuthorityEpoch || run.AuthorityEpoch != _state.AuthorityEpoch)
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, run.AttemptId, RunAttempt: ToDto(run));
        if (!IsCurrentRun(run) || run.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, run.AttemptId, RunAttempt: ToDto(run));
        if (write.Fence != run.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        if (run.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, run.AttemptId, RunAttempt: ToDto(run));
        if (run.Lease is null || run.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, run.AttemptId, RunAttempt: ToDto(run));
        if (!Same(run.Lease.ExecutorId, executorId)
            || (!Blank(leaseId) && !Same(run.Lease.LeaseId, leaseId)))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, run.AttemptId, RunAttempt: ToDto(run));
        return new AttemptWriteResult(AttemptWriteStatus.Duplicate, run.AttemptId, RunAttempt: ToDto(run));
    }

    private AttemptWriteResult ClassifyReviewLeaseReplay(
        ReviewAttemptRecord review,
        string executorId,
        AttemptWriteReference? write = null,
        string? claimDeliveryKey = null)
    {
        if (review.AuthorityEpoch != _state.AuthorityEpoch
            || (write is not null && write.AuthorityEpoch != _state.AuthorityEpoch))
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
        if (write is not null && write.Fence != review.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.Lease is null || review.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!Same(review.Lease.ExecutorId, executorId))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!Blank(claimDeliveryKey) && !Same(review.CurrentClaimDeliveryKey, claimDeliveryKey))
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId,
                "The claim delivery belongs to an older review lease.", ReviewAttempt: ToDto(review));
        return new AttemptWriteResult(AttemptWriteStatus.Duplicate, review.AttemptId, ReviewAttempt: ToDto(review));
    }

    private AttemptWriteResult ValidateReviewWriteLocked(AttemptWriteReference write, string idempotencyScope)
    {
        if (Blank(write.AttemptId) || write.Fence <= 0 || write.AuthorityEpoch <= 0 || Blank(write.IdempotencyKey))
            return new AttemptWriteResult(
                AttemptWriteStatus.Invalid,
                Normalize(write.AttemptId),
                "AttemptId, Fence, AuthorityEpoch, and IdempotencyKey are required.");
        var review = FindReview(write.AttemptId);
        if (review is null) return new AttemptWriteResult(AttemptWriteStatus.NotFound, write.AttemptId);
        if (review.IdempotencyKeys.Contains(DeliveryKey(idempotencyScope, write.IdempotencyKey)))
            return new AttemptWriteResult(AttemptWriteStatus.Duplicate, review.AttemptId, ReviewAttempt: ToDto(review));
        if (write.AuthorityEpoch != _state.AuthorityEpoch || review.AuthorityEpoch != _state.AuthorityEpoch)
            return new AttemptWriteResult(AttemptWriteStatus.AuthorityEpochMismatch, review.AttemptId, ReviewAttempt: ToDto(review));
        if (!IsCurrentReview(review) || review.State == AttemptLifecycleState.Superseded)
            return new AttemptWriteResult(AttemptWriteStatus.Superseded, review.AttemptId, ReviewAttempt: ToDto(review));
        if (write.Fence != review.LastFence)
            return new AttemptWriteResult(AttemptWriteStatus.StaleFence, review.AttemptId, ReviewAttempt: ToDto(review));
        if (review.State != AttemptLifecycleState.Leased)
            return new AttemptWriteResult(AttemptWriteStatus.InvalidState, review.AttemptId,
                $"ReviewAttempt is {review.State} and no longer has write authority.", ReviewAttempt: ToDto(review));
        if (review.Lease is null || review.Lease.ExpiresAt <= _utcNow())
            return new AttemptWriteResult(AttemptWriteStatus.LeaseExpired, review.AttemptId, ReviewAttempt: ToDto(review));
        return new AttemptWriteResult(AttemptWriteStatus.Accepted, review.AttemptId, ReviewAttempt: ToDto(review));
    }

    private AttemptLeaseRecord NewLease(
        string executorId,
        string hostId,
        long fence,
        int? ttlSeconds,
        DateTime now,
        string? executorDisplayName = null,
        string? backendName = null,
        int processId = 0) => new()
    {
        LeaseId = Guid.NewGuid().ToString("N"),
        Fence = fence,
        AuthorityEpoch = _state.AuthorityEpoch,
        ExecutorId = Normalize(executorId),
        HostId = Normalize(hostId),
        AcquiredAt = now,
        ExpiresAt = now.Add(NormalizeTtl(ttlSeconds)),
        LastHeartbeat = now,
        ExecutorDisplayName = NormalizeNull(executorDisplayName),
        BackendName = NormalizeNull(backendName),
        ProcessId = processId,
    };

    private long NextFenceLocked(string taskKey)
    {
        var key = Normalize(taskKey);
        var last = _state.LastFenceByTask.TryGetValue(key, out var value) ? value : 0;
        _state.LastFenceByTask[key] = last + 1;
        return last + 1;
    }

    private bool IsCurrentRun(RunAttemptRecord run)
        => _state.CurrentRunByTask.TryGetValue(run.TaskKey, out var id) && Same(id, run.AttemptId);

    private bool IsCurrentReview(ReviewAttemptRecord review)
        => _state.CurrentReviewByTask.TryGetValue(review.TaskKey, out var id) && Same(id, review.AttemptId);

    private RunAttemptRecord? CurrentRun(string taskKey)
        => _state.CurrentRunByTask.TryGetValue(Normalize(taskKey), out var id) ? FindRun(id) : null;

    private ReviewAttemptRecord? CurrentReview(string taskKey)
        => _state.CurrentReviewByTask.TryGetValue(Normalize(taskKey), out var id) ? FindReview(id) : null;

    private RunAttemptRecord? FindRun(string id) => _state.RunAttempts.FirstOrDefault(x => Same(x.AttemptId, id));
    private ReviewAttemptRecord? FindReview(string id) => _state.ReviewAttempts.FirstOrDefault(x => Same(x.AttemptId, id));
    private RunAttemptRecord? FindIdempotentRun(string taskKey, string key) => _state.RunAttempts.FirstOrDefault(
        x => Same(x.TaskKey, taskKey) && x.IdempotencyKeys.Contains(key));
    private ReviewAttemptRecord? FindIdempotentReview(string taskKey, string key) => _state.ReviewAttempts.FirstOrDefault(
        x => Same(x.TaskKey, taskKey) && x.IdempotencyKeys.Contains(key));

    private AuthorityState Load()
    {
        if (_path is null || !File.Exists(_path)) return new AuthorityState();
        try
        {
            return JsonSerializer.Deserialize<AuthorityState>(File.ReadAllText(_path), JsonOptions) ?? new AuthorityState();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Attempt authority store '{_path}' could not be loaded; refusing to reset fences.", ex);
        }
    }

    private void NormalizeLoadedState()
    {
        var migrateUnscopedIdempotency = _state.SchemaVersion < 2;
        _state.LastFenceByTask = new Dictionary<string, long>(
            _state.LastFenceByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentRunByTask = new Dictionary<string, string>(
            _state.CurrentRunByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentReviewByTask = new Dictionary<string, string>(
            _state.CurrentReviewByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.CurrentSubjectByTask = new Dictionary<string, ReviewSubjectRecord>(
            _state.CurrentSubjectByTask ?? [], StringComparer.OrdinalIgnoreCase);
        _state.RunAttempts ??= [];
        _state.ReviewAttempts ??= [];
        foreach (var run in _state.RunAttempts)
        {
            run.IdempotencyKeys ??= [];
            if (migrateUnscopedIdempotency)
                run.IdempotencyKeys = ExpandLegacyDeliveryKeys(
                    run.IdempotencyKeys, ["acquire", "renew", "release", "write", "evidence", "settle"]);
            run.EvidenceDigests ??= [];
        }
        foreach (var review in _state.ReviewAttempts)
        {
            review.IdempotencyKeys ??= [];
            if (migrateUnscopedIdempotency)
                review.IdempotencyKeys = ExpandLegacyDeliveryKeys(
                    review.IdempotencyKeys, ["create", "renew", "claim", "settle"]);
            review.Reports ??= [];
            review.Subject.EvidenceDigestInputs ??= [];
            if (Blank(review.CurrentClaimDeliveryKey)
                && review.State == AttemptLifecycleState.Leased)
            {
                var historicalClaims = review.IdempotencyKeys
                    .Where(key => key.StartsWith("claim:", StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                // A single historical claim is unambiguous and can retain
                // restart idempotency. Multiple claims imply a takeover chain;
                // leave it unset so any old replay fails closed.
                if (historicalClaims.Count == 1)
                    review.CurrentClaimDeliveryKey = historicalClaims[0];
            }
        }
        _state.SchemaVersion = 2;
    }

    private static HashSet<string> ExpandLegacyDeliveryKeys(
        IEnumerable<string> keys,
        IReadOnlyList<string> scopes) => keys
        .SelectMany(key => scopes.Select(scope => DeliveryKey(scope, key)))
        .ToHashSet(StringComparer.Ordinal);

    private void PersistLocked()
    {
        if (_path is null) return;
        try
        {
            _writer.Write(_path, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch
        {
            // No failed disk write may leave this process with authority that a
            // restarted Task Server would not recognize. Restore the last
            // durable snapshot before surfacing the persistence failure.
            _state = Load();
            NormalizeLoadedState();
            if (_state.AuthorityEpoch <= 0) _state.AuthorityEpoch = 1;
            throw;
        }
    }

    private static string SubjectId(string repositoryId, string sha, string runId, string requirementsHash, string policyHash, IReadOnlyList<string> evidence)
        => "subject_" + Hash(string.Join("\n", [Normalize(repositoryId), sha, Normalize(runId), Normalize(requirementsHash), Normalize(policyHash), .. evidence]))[..24];

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
    private static string DeliveryKey(string scope, string key) => $"{scope}:{Normalize(key)}";
    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
    private static bool Terminal(AttemptLifecycleState state) => state is AttemptLifecycleState.Completed or AttemptLifecycleState.Failed or AttemptLifecycleState.Cancelled or AttemptLifecycleState.Superseded;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
    private static string? NormalizeNull(string? value) => Blank(value) ? null : value!.Trim();
    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static TimeSpan NormalizeTtl(int? seconds)
    {
        var ttl = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : TimeSpan.FromMinutes(2);
        return ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
    }

    private static AttemptWriteResult InvalidRun(string message) => new(AttemptWriteStatus.Invalid, string.Empty, message);
    private static AttemptLeaseDto? ToDto(AttemptLeaseRecord? lease) => lease is null ? null : new(
        lease.LeaseId, lease.Fence, lease.AuthorityEpoch, lease.ExecutorId, lease.HostId,
        lease.AcquiredAt, lease.ExpiresAt, lease.LastHeartbeat,
        lease.ExecutorDisplayName, lease.BackendName, lease.ProcessId);
    private static ReviewSubjectDto ToDto(ReviewSubjectRecord subject) => new(
        subject.SubjectId, subject.RepositoryId, subject.ExpectedResultSha, subject.SourceRunAttemptId,
        subject.TaskRequirementsHash, subject.ReviewPolicyHash, subject.EvidenceDigestInputs, subject.CreatedAt);
    private static RunAttemptDto ToDto(RunAttemptRecord run) => new(
        run.AttemptId, run.TaskKey, run.RepositoryId, run.SourceAttemptId, run.State, ToDto(run.Lease),
        run.LastFence, run.AuthorityEpoch, run.CreatedAt, run.TerminalAt, run.ResultSha,
        run.TerminalOutcome, run.TerminalReason, run.EvidenceDigests);
    private static ReviewAttemptDto ToDto(ReviewAttemptRecord review) => new(
        review.AttemptId, review.TaskKey, review.RepositoryId, review.SourceRunAttemptId,
        review.SourceReviewAttemptId, ToDto(review.Subject), review.State, ToDto(review.Lease),
        review.LastFence, review.AuthorityEpoch, review.CreatedAt, review.TerminalAt, review.Outcome,
        review.FailureClassification, review.TestedResultSha, review.TerminalReason,
        review.Reports.Select(ToDto).ToList());
    private static ReviewReportDeliveryDto ToDto(ReviewReportDeliveryRecord report) => new(
        report.IdempotencyKey, report.Fence, report.AuthorityEpoch, report.MaterializedResultSha,
        report.Outcome, report.FailureClassification, report.Reason, report.AuthorityStatus, report.ReceivedAt);
    private static ReviewReportDeliveryRecord ToReport(
        SettleReviewAttemptRequest request,
        AttemptWriteStatus authorityStatus,
        DateTime receivedAt) => new()
    {
        IdempotencyKey = request.Write.IdempotencyKey,
        Fence = request.Write.Fence,
        AuthorityEpoch = request.Write.AuthorityEpoch,
        MaterializedResultSha = Normalize(request.MaterializedResultSha).ToLowerInvariant(),
        Outcome = request.Outcome,
        FailureClassification = NormalizeNull(request.FailureClassification),
        Reason = NormalizeNull(request.Reason),
        AuthorityStatus = authorityStatus,
        ReceivedAt = receivedAt,
    };

    private sealed class AuthorityState
    {
        public int SchemaVersion { get; set; } = 2;
        public long AuthorityEpoch { get; set; } = 1;
        public Dictionary<string, long> LastFenceByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RunAttemptRecord> RunAttempts { get; set; } = [];
        public List<ReviewAttemptRecord> ReviewAttempts { get; set; } = [];
        public Dictionary<string, string> CurrentRunByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CurrentReviewByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReviewSubjectRecord> CurrentSubjectByTask { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AttemptLeaseRecord
    {
        public string LeaseId { get; set; } = string.Empty;
        public long Fence { get; set; }
        public long AuthorityEpoch { get; set; }
        public string ExecutorId { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public DateTime AcquiredAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public string? ExecutorDisplayName { get; set; }
        public string? BackendName { get; set; }
        public int ProcessId { get; set; }
    }

    private sealed class RunAttemptRecord
    {
        public string AttemptId { get; set; } = string.Empty;
        public string TaskKey { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string? SourceAttemptId { get; set; }
        public AttemptLifecycleState State { get; set; }
        public AttemptLeaseRecord? Lease { get; set; }
        public long LastFence { get; set; }
        public long AuthorityEpoch { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? TerminalAt { get; set; }
        public string? ResultSha { get; set; }
        public string? TerminalOutcome { get; set; }
        public string? TerminalReason { get; set; }
        public List<string> EvidenceDigests { get; set; } = [];
        public HashSet<string> IdempotencyKeys { get; set; } = [];
    }

    private sealed class ReviewAttemptRecord
    {
        public string AttemptId { get; set; } = string.Empty;
        public string TaskKey { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string SourceRunAttemptId { get; set; } = string.Empty;
        public string? SourceReviewAttemptId { get; set; }
        public ReviewSubjectRecord Subject { get; set; } = new();
        public AttemptLifecycleState State { get; set; }
        public AttemptLeaseRecord? Lease { get; set; }
        public long LastFence { get; set; }
        public long AuthorityEpoch { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? TerminalAt { get; set; }
        public ReviewTerminalOutcome? Outcome { get; set; }
        public string? FailureClassification { get; set; }
        public string? TestedResultSha { get; set; }
        public string? TerminalReason { get; set; }
        public string? CurrentClaimDeliveryKey { get; set; }
        public HashSet<string> IdempotencyKeys { get; set; } = [];
        public List<ReviewReportDeliveryRecord> Reports { get; set; } = [];
    }

    private sealed class ReviewReportDeliveryRecord
    {
        public string IdempotencyKey { get; set; } = string.Empty;
        public long Fence { get; set; }
        public long AuthorityEpoch { get; set; }
        public string MaterializedResultSha { get; set; } = string.Empty;
        public ReviewTerminalOutcome Outcome { get; set; }
        public string? FailureClassification { get; set; }
        public string? Reason { get; set; }
        public AttemptWriteStatus AuthorityStatus { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    private sealed class ReviewSubjectRecord
    {
        public string SubjectId { get; set; } = string.Empty;
        public string RepositoryId { get; set; } = string.Empty;
        public string ExpectedResultSha { get; set; } = string.Empty;
        public string SourceRunAttemptId { get; set; } = string.Empty;
        public string TaskRequirementsHash { get; set; } = string.Empty;
        public string ReviewPolicyHash { get; set; } = string.Empty;
        public List<string> EvidenceDigestInputs { get; set; } = [];
        public DateTime CreatedAt { get; set; }
    }
}
