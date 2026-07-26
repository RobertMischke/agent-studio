using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Tranche-0 compatibility mount for the versioned Remote Review plane. The
/// monolith remains the single task and AttemptAuthority writer; this adapter
/// only translates the published Task Server contracts used by agent-runner.
/// </summary>
public static class V1ReviewPlaneEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapV1ReviewPlaneEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/protocol", () => Results.Ok(Protocol()));
        api.MapPost("/protocol/compatibility", (Contract.ProtocolCompatibilityRequest request) =>
        {
            var supported = Contract.TaskServerProtocol.Supports(request.ProtocolVersion)
                            && request.ClientKind is "runner" or "review-runner";
            var response = new Contract.ProtocolCompatibilityResponse(
                supported,
                Protocol(),
                supported
                    ? null
                    : $"{request.ClientKind} protocol {request.ProtocolVersion} is not supported.");
            return supported
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status426UpgradeRequired);
        });

        api.MapPut("/runners/{runnerId}", (
            HttpContext context,
            string runnerId,
            Contract.RegisterRunnerRequest request,
            V1ReviewExecutorRegistry registry) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            try
            {
                return Results.Ok(registry.Register(runnerId, request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError("runner-role-conflict", exception.Message));
            }
        });

        api.MapPut("/runners/{runnerId}/capabilities", (
            HttpContext context,
            string runnerId,
            Contract.CapabilityAdvertisementRequest request,
            V1ReviewExecutorRegistry registry) =>
        {
            if (!RunnerMatches(context, runnerId))
                return Results.Unauthorized();
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new Contract.ApiError(
                    "runner-id-mismatch",
                    "Route and capability runner ids differ."));
            try
            {
                return Results.Ok(registry.AdvertiseCapabilities(runnerId, request));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new Contract.ApiError("invalid-request", exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new Contract.ApiError(
                    "capability-advertisement-conflict",
                    exception.Message));
            }
        });

        api.MapPost("/runners/{runnerId}/review-claims", async (
            HttpContext context,
            string runnerId,
            Contract.ReviewClaimRequest request,
            V1ReviewExecutorRegistry registry,
            AttemptAuthorityService authority,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Projects.ProjectSettingsService settings,
            HumanReviewEscalation escalation,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, runnerId)
                || !string.Equals(runnerId, request.ExecutorId, StringComparison.Ordinal))
                return Results.Unauthorized();
            if (request.AvailableSlots <= 0)
                return Results.Ok(new Contract.ReviewClaimResponse(
                    "empty", Message: "Review executor has no available slot."));
            if (!registry.TryGetReviewExecutor(runnerId, request.InstanceId, out var executor))
                return Results.Conflict(new Contract.ApiError(
                    "review-executor-not-registered",
                    "Register this identity with the review-executor capability before claiming."));
            if (!executor.Capabilities.Contains(Contract.ReviewCapabilities.BaselineComparison))
                return Results.Conflict(new Contract.ApiError(
                    "review-baseline-comparison-required",
                    "Update this Review Executor to one that advertises baseline-comparison support."));

            foreach (var legacy in authority.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope())
            {
                var task = FindTask(scanner, legacy.TaskKey);
                if (task is null
                    || !string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                    continue;
                var moved = await escalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
                    "The immutable ReviewSubject has no persisted Result-Envelope and cannot be materialized.",
                    ct);
                if (moved.Status != MoveJobStatus.Success)
                {
                    return Results.Json(
                        new Contract.ApiError(
                            "review-subject-escalation-failed",
                            $"Legacy ReviewSubject was terminalized, but its Escalated lane write failed: {moved.Status} {moved.Message}"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var claimed = authority.ClaimNextReview(
                runnerId,
                executor.HostId,
                request.InstanceId,
                request.RequestedTtlSeconds);
            if (claimed.Status == AttemptWriteStatus.NotFound)
                return Results.Ok(new Contract.ReviewClaimResponse(
                    "empty", Message: "No current immutable ReviewAttempt is queued."));
            if (!claimed.Accepted || claimed.ReviewAttempt is null)
                return AttemptError(claimed);

            var review = claimed.ReviewAttempt;
            var subject = ToSubject(review, scanner, projects, settings);
            var lease = ToLease(review);
            return Results.Ok(new Contract.ReviewClaimResponse(
                "claimed",
                ToAttempt(review),
                subject,
                lease));
        });

        api.MapGet("/reviews/attempts/{attemptId}", (
            string attemptId,
            AttemptAuthorityService authority) =>
            authority.GetReview(attemptId) is { } review
                ? Results.Ok(ToAttempt(review))
                : Results.NotFound(new Contract.ApiError("not-found", "ReviewAttempt was not found.")));

        api.MapPost("/reviews/attempts/{attemptId}/lease/renew", (
            HttpContext context,
            string attemptId,
            Contract.ReviewLeaseRenewRequest request,
            AttemptAuthorityService authority) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    out var review,
                    out var error))
                return error!;

            var renewed = authority.RenewReview(
                new AttemptWriteReference(
                    attemptId,
                    request.Fence,
                    request.AuthorityEpoch,
                    request.IdempotencyKey),
                request.ExecutorId,
                request.RequestedTtlSeconds);
            return renewed.Accepted && renewed.ReviewAttempt is not null
                ? Results.Ok(ToLease(renewed.ReviewAttempt))
                : AttemptError(renewed);
        });

        api.MapPost("/reviews/attempts/{attemptId}/report", async (
            HttpContext context,
            string attemptId,
            Contract.ReviewReportRequest request,
            AttemptAuthorityService authority,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Projects.ProjectSettingsService settings,
            TaskTransitionService transitions,
            HumanReviewEscalation escalation,
            TimelineLog timeline,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    out var current,
                    out var error,
                    allowTerminal: true))
                return error!;
            var currentReview = current!;
            var materializableRepository = MaterializableRepository(
                currentReview, scanner, projects, settings);
            if (!string.Equals(
                    request.Workspace.RepositoryId,
                    materializableRepository.RepositoryId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.Workspace.ExpectedResultSha,
                    currentReview.Subject.ExpectedResultSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new Contract.ApiError(
                    "review-subject-mismatch",
                    "Review workspace does not identify the immutable ReviewSubject."));
            }

            if (!TryOutcome(request.Outcome, out var outcome))
                return Results.BadRequest(new Contract.ApiError(
                    "invalid-review-outcome",
                    "Outcome must be Pass, ProductFailure, ReviewInfra, Inconclusive, or Cancellation."));

            var settled = authority.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    attemptId,
                    request.Fence,
                    request.AuthorityEpoch,
                    request.IdempotencyKey),
                request.Workspace.ActualHead,
                outcome,
                request.FailureClassification,
                request.Summary));
            if (!settled.Accepted || settled.ReviewAttempt is null)
                return AttemptError(settled);

            var task = FindTask(scanner, settled.ReviewAttempt.TaskKey);
            if (task is null)
                return Results.Json(
                    new Contract.ApiError("task-not-found", "Review task was not found in the monolith store."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var receivedAt = settled.ReviewAttempt.Reports
                .LastOrDefault(report => string.Equals(
                    report.IdempotencyKey,
                    request.IdempotencyKey,
                    StringComparison.Ordinal))
                ?.ReceivedAt
                ?? DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(request, Json);
            var reportHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant();
            string evidenceFile;
            try
            {
                evidenceFile = await RemoteReviewReportEvidence.WriteAsync(
                    task.FolderPath,
                    attemptId,
                    settled.ReviewAttempt.Subject.SubjectId,
                    request,
                    reportHash,
                    receivedAt,
                    ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Json(
                    new Contract.ApiError(
                        "review-evidence-write-failed",
                        $"Review grade is durable, but its task evidence file could not be written: {exception.Message}"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var infrastructureFailure = string.Equals(
                request.Outcome,
                "ReviewInfra",
                StringComparison.OrdinalIgnoreCase);
            var retry = infrastructureFailure
                        && authority.HasReviewInfrastructureRetryBudget(settled.ReviewAttempt.AttemptId);
            var taskState = TaskStates.AutoReview;
            if (retry)
            {
                var review = settled.ReviewAttempt;
                var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                    review.TaskKey,
                    review.RepositoryId,
                    review.Subject.ExpectedResultSha,
                    review.SourceRunAttemptId,
                    review.Subject.TaskRequirementsHash,
                    review.Subject.ReviewPolicyHash,
                    review.Subject.EvidenceDigestInputs,
                    $"v1-review-retry:{attemptId}:{request.IdempotencyKey}",
                    review.AttemptId,
                    review.Subject.RepositoryUrl,
                    review.Subject.ResultRef,
                    review.Subject.Plan));
                if (!created.Accepted)
                    return AttemptError(created);
            }
            else if (!infrastructureFailure)
            {
                if (string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    var moved = await transitions.MoveAsync(
                        task.Id,
                        TaskStates.HumanReview,
                        task.WatchPath,
                        ct,
                        cause: $"remote-review:{attemptId}",
                        authorityWrite: new AttemptWriteReference(
                            attemptId,
                            request.Fence,
                            request.AuthorityEpoch,
                            $"lane:{request.IdempotencyKey}"),
                        suppressProductExecution: true,
                        expectedSourceState: TaskStates.AutoReview);
                    if (moved.Status == MoveJobStatus.SourceStateMismatch)
                    {
                        var racedTask = FindTask(scanner, settled.ReviewAttempt.TaskKey);
                        if (racedTask is null)
                        {
                            return Results.Json(
                                new Contract.ApiError(
                                    "task-not-found",
                                    "Review task disappeared while its report was being recorded."),
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        }
                        taskState = racedTask.State;
                        RecordPostAcceptanceReportIfTerminal(timeline, racedTask, attemptId, request, evidenceFile);
                    }
                    else if (moved.Status != MoveJobStatus.Success)
                    {
                        return Results.Json(
                            new Contract.ApiError(
                                "review-lane-write-failed",
                                $"Review grade is durable, but the Human Review lane write failed: {moved.Status} {moved.Message}"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    else
                    {
                        taskState = TaskStates.HumanReview;
                    }
                }
                else if (string.Equals(task.State, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase))
                {
                    taskState = TaskStates.HumanReview;
                }
                else
                {
                    taskState = task.State;
                    RecordPostAcceptanceReportIfTerminal(timeline, task, attemptId, request, evidenceFile);
                }
            }
            else
            {
                var review = settled.ReviewAttempt;
                if (string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    var moved = await escalation.EscalateAsync(
                        task.Id,
                        task.WatchPath,
                        task.ProjectName,
                        HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
                        $"The immutable ReviewSubject exhausted its budget of {AttemptAuthorityService.ReviewInfrastructureRetryBudget} infrastructure retries and cannot be materialized.",
                        ct);
                    if (moved.Status != MoveJobStatus.Success)
                    {
                        return Results.Json(
                            new Contract.ApiError(
                                "review-subject-escalation-failed",
                                $"Review result is durable, but the Escalated lane write failed: {moved.Status} {moved.Message}"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    taskState = TaskStates.Escalated;
                }
                else if (string.Equals(task.State, TaskStates.Escalated, StringComparison.OrdinalIgnoreCase))
                {
                    taskState = TaskStates.Escalated;
                }
                else
                {
                    taskState = task.State;
                    RecordPostAcceptanceReportIfTerminal(timeline, task, attemptId, request, evidenceFile);
                }
            }

            return Results.Ok(new Contract.ReviewReportDto(
                "rrpt_" + HashId($"{attemptId}:{request.IdempotencyKey}"),
                attemptId,
                settled.ReviewAttempt.Subject.SubjectId,
                request.Outcome,
                request.FailureClassification,
                request.Summary,
                reportHash,
                receivedAt,
                retry,
                taskState));
        });

        api.MapPost("/reviews/attempts/{attemptId}/cleanup", (
            HttpContext context,
            string attemptId,
            Contract.ReviewCleanupRequest request,
            AttemptAuthorityService authority) =>
        {
            if (!RunnerMatches(context, request.ExecutorId))
                return Results.Unauthorized();
            if (!TryValidateLease(
                    authority,
                    attemptId,
                    request.ExecutorId,
                    request.InstanceId,
                    request.LeaseId,
                    request.Fence,
                    request.AuthorityEpoch,
                    out var review,
                    out var error,
                    allowTerminal: true))
                return error!;
            return Results.Ok(new Contract.ReviewCleanupResponse(
                request.WorkspaceRemoved ? "cleaned" : "cleanup-failed",
                attemptId,
                DateTime.UtcNow,
                !request.WorkspaceRemoved
                && review!.Outcome == ReviewTerminalOutcome.InfrastructureFailure));
        });

        api.MapGet("/runs/{runId}/result-handoff", (
            string runId,
            AttemptAuthorityService authority) =>
            authority.GetResultHandoff(runId) is { } handoff
                ? Results.Ok(handoff)
                : Results.NotFound(new Contract.ApiError(
                    "result-envelope-not-found",
                    "This run has no persisted immutable result envelope.")));
    }

    private static Contract.ProtocolRangeDto Protocol()
    {
        var version = typeof(V1ReviewPlaneEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return new Contract.ProtocolRangeDto(
            Contract.TaskServerProtocol.Current,
            Contract.TaskServerProtocol.MinimumSupported,
            Contract.TaskServerProtocol.MaximumSupported,
            version,
            "orchestrator-monolith",
            ["runner", "review-runner"],
            ["review-plane"]);
    }

    private static void RecordPostAcceptanceReportIfTerminal(
        TimelineLog timeline,
        TaskInfo task,
        string attemptId,
        Contract.ReviewReportRequest request,
        string evidenceFile)
    {
        if (task.State is not (TaskStates.Completed or TaskStates.Archive))
            return;
        if (timeline.ReadAll(task.FolderPath).Any(item =>
                item.Kind == TimelineEventKinds.PostAcceptanceReviewReportRecorded
                && string.Equals(item.RunId, attemptId, StringComparison.Ordinal)))
        {
            return;
        }

        timeline.Append(
            task.FolderPath,
            TimelineEventKinds.PostAcceptanceReviewReportRecorded,
            TimelineActors.System,
            "post-acceptance review report recorded",
            runId: attemptId,
            payloadRef: evidenceFile,
            details: new Dictionary<string, string>
            {
                ["attemptId"] = attemptId,
                ["fence"] = request.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["authorityEpoch"] = request.AuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["outcome"] = request.Outcome,
                ["lane"] = task.State,
            });
    }

    private static Contract.ReviewSubjectDto ToSubject(
        ReviewAttemptDto review,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        var materializableRepository = MaterializableRepository(
            review, scanner, projects, settings);
        var task = FindTask(scanner, review.TaskKey);
        var project = task is null
            ? null
            : projects.FindByStorageLocation(task.WatchPath)
              ?? projects.FindByIdOrDisplayName(task.ProjectName);
        var integrationRef = task is null
            ? null
            : IntegrationRef(settings.Get(task.ProjectName).IntegrationBranch);
        var plan = review.Subject.Plan
                   ?? FallbackPlan(project?.RepositoryPath, task?.ProjectName, settings, integrationRef);
        if (string.IsNullOrWhiteSpace(plan.IntegrationRef) && integrationRef is not null)
            plan = plan with { IntegrationRef = integrationRef };
        return new Contract.ReviewSubjectDto(
            review.Subject.SubjectId,
            task?.Id ?? review.TaskKey,
            review.SourceRunAttemptId,
            materializableRepository.RepositoryId,
            materializableRepository.RepositoryUrl,
            review.Subject.ExpectedResultSha,
            review.Subject.ResultRef ?? review.Subject.ExpectedResultSha,
            null,
            null,
            null,
            review.Subject.ReviewPolicyHash,
            plan,
            review.Subject.CreatedAt);
    }

    private static (string RepositoryId, string? RepositoryUrl) MaterializableRepository(
        ReviewAttemptDto review,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        var task = FindTask(scanner, review.TaskKey);
        var project = task is null
            ? null
            : projects.FindByStorageLocation(task.WatchPath)
              ?? projects.FindByIdOrDisplayName(task.ProjectName);
        var repository = task is null
            ? null
            : RemoteProjectRepositoryResolver.Resolve(
                project,
                settings.Get(task.ProjectName).IntegrationBranch);
        var repositoryUrl = review.Subject.RepositoryUrl ?? repository?.RepositoryUrl;
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)
                           ?? review.RepositoryId;
        return (repositoryId, repositoryUrl);
    }

    private static Contract.ReviewPlanDto FallbackPlan(
        string? repositoryPath,
        string? projectName,
        AgentStudio.Projects.ProjectSettingsService settings,
        string? integrationRef)
    {
        var profile = string.IsNullOrWhiteSpace(projectName)
            ? null
            : settings.Get(projectName).BuildProfile;
        var verify = VerifyCommandPlanner.Plan(repositoryPath ?? string.Empty, profile);
        var commands = verify.Commands
            .Select((command, index) =>
            {
                var shellCommand = string.IsNullOrWhiteSpace(command.WorkingSubdir)
                    ? command.Command
                    : $"cd -- {ShellQuote(command.WorkingSubdir)} && {command.Command}";
                return new Contract.ReviewCommandDto(
                    $"verify-{index + 1}",
                    command.Kind == VerifyCommandKind.Lint ? "lint" : "build-tests",
                    "sh",
                    ["-lc", shellCommand],
                    CompareToBaseline: command.Kind == VerifyCommandKind.Test);
            })
            .ToList();
        if (commands.Count == 0)
        {
            commands.Add(new Contract.ReviewCommandDto(
                "verify-subject", "completion", "git", ["rev-parse", "--verify", "HEAD"]));
        }
        return new Contract.ReviewPlanDto(
            commands,
            commands.Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IntegrationRef: integrationRef);
    }

    private static string? IntegrationRef(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var value = branch.Trim();
        if (value.StartsWith("refs/", StringComparison.Ordinal)) return value;
        if (value.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            value = value["origin/".Length..];
        return $"refs/heads/{value}";
    }

    private static Contract.ReviewAttemptDto ToAttempt(ReviewAttemptDto review)
    {
        var attemptNumber = 1;
        if (!string.IsNullOrWhiteSpace(review.SourceReviewAttemptId)) attemptNumber = 2;
        return new Contract.ReviewAttemptDto(
            review.AttemptId,
            review.Subject.SubjectId,
            review.TaskKey,
            attemptNumber,
            review.State.ToString().ToLowerInvariant(),
            review.Lease?.ExecutorId,
            review.Lease?.HostId,
            review.LastFence,
            review.CreatedAt,
            review.TerminalAt,
            null,
            review.Outcome?.ToString(),
            review.FailureClassification);
    }

    private static Contract.ReviewLeaseDto ToLease(ReviewAttemptDto review)
    {
        var lease = review.Lease
                    ?? throw new InvalidOperationException("Claimed ReviewAttempt has no lease.");
        var resourceNamespace =
            $"review-{SafeSegment(review.AttemptId).ToLowerInvariant()}-f{review.LastFence}";
        return new Contract.ReviewLeaseDto(
            lease.LeaseId,
            review.AttemptId,
            review.Subject.SubjectId,
            lease.ExecutorId,
            lease.ClientId ?? lease.ExecutorId,
            lease.HostId,
            review.LastFence,
            lease.AcquiredAt,
            lease.ExpiresAt,
            "active",
            resourceNamespace,
            PortBase(review.AttemptId, review.LastFence),
            review.AuthorityEpoch);
    }

    private static bool TryValidateLease(
        AttemptAuthorityService authority,
        string attemptId,
        string executorId,
        string instanceId,
        string leaseId,
        long fence,
        long authorityEpoch,
        out ReviewAttemptDto? review,
        out IResult? error,
        bool allowTerminal = false)
    {
        review = authority.GetReview(attemptId);
        if (review is null)
        {
            error = Results.NotFound(new Contract.ApiError("not-found", "ReviewAttempt was not found."));
            return false;
        }
        var lease = review.Lease;
        if (lease is null
            || !string.Equals(lease.ExecutorId, executorId, StringComparison.Ordinal)
            || !string.Equals(lease.ClientId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || review.LastFence != fence
            || review.AuthorityEpoch != authorityEpoch)
        {
            error = Results.Conflict(new Contract.ApiError(
                "stale-review-authority",
                "AttemptId, lease, executor, instance, fence, or authority epoch is stale."));
            return false;
        }
        if (!allowTerminal && review.State != AttemptLifecycleState.Leased)
        {
            error = Results.Conflict(new Contract.ApiError(
                "review-attempt-not-leased",
                "ReviewAttempt is not currently leased."));
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryOutcome(string value, out ReviewTerminalOutcome outcome)
    {
        outcome = value.Trim().ToLowerInvariant() switch
        {
            "pass" => ReviewTerminalOutcome.Pass,
            "productfailure" => ReviewTerminalOutcome.ProductFailure,
            "reviewinfra" => ReviewTerminalOutcome.InfrastructureFailure,
            "inconclusive" => ReviewTerminalOutcome.Inconclusive,
            "cancellation" => ReviewTerminalOutcome.Cancellation,
            _ => (ReviewTerminalOutcome)(-1),
        };
        return Enum.IsDefined(outcome);
    }

    private static IResult AttemptError(AttemptWriteResult result)
    {
        var error = new Contract.ApiError(
            result.Status.ToString(),
            result.Message ?? $"Review authority rejected the write as {result.Status}.");
        return result.Status switch
        {
            AttemptWriteStatus.NotFound => Results.NotFound(error),
            AttemptWriteStatus.Invalid => Results.BadRequest(error),
            _ => Results.Conflict(error),
        };
    }

    private static TaskInfo? FindTask(TaskScannerService scanner, string taskKey)
        => scanner.ScanAllJobs().FirstOrDefault(task =>
            string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Key, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Id, taskKey, StringComparison.OrdinalIgnoreCase));

    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);

    private static int PortBase(string attemptId, long fence)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{attemptId}:{fence}"));
        var slot = BitConverter.ToUInt16(bytes, 0) % 4000;
        return 24000 + slot * 8;
    }

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string HashId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];
}

public sealed class V1ReviewExecutorRegistry
{
    private readonly ConcurrentDictionary<string, Registration> _registrations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityState> _capabilityStates =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Contract.RunnerDto Register(string runnerId, Contract.RegisterRunnerRequest request)
    {
        if (!Contract.TaskServerProtocol.Supports(request.ProtocolVersion))
            throw new ArgumentException("Runner protocol is not supported.");
        if (string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(request.HostId)
            || string.IsNullOrWhiteSpace(request.InstanceId))
            throw new ArgumentException("Runner, host, and instance ids are required.");
        var capabilities = request.Capabilities ?? [];
        if (!capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal)
            || capabilities.Contains(Contract.ReviewCapabilities.CodingExecutor, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "The monolith V1 mount admits a separate review-executor identity only.");

        var now = DateTime.UtcNow;
        Registration registration;
        lock (_gate)
        {
            registration = _registrations.AddOrUpdate(
                runnerId,
                _ => new Registration(
                    request.Name,
                    request.HostId,
                    request.InstanceId,
                    request.RunnerVersion,
                    request.ProtocolVersion,
                    capabilities.ToHashSet(StringComparer.Ordinal),
                    now,
                    now),
                (_, existing) =>
                {
                    if (!existing.Capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor))
                        throw new InvalidOperationException(
                            "A coding identity cannot be changed into a review identity.");
                    return existing with
                    {
                        Name = request.Name,
                        HostId = request.HostId,
                        InstanceId = request.InstanceId,
                        RunnerVersion = request.RunnerVersion,
                        ProtocolVersion = request.ProtocolVersion,
                        Capabilities = capabilities.ToHashSet(StringComparer.Ordinal),
                        LastSeenAt = now,
                    };
                });
            if (_capabilityStates.TryGetValue(runnerId, out var capabilityState)
                && !string.Equals(
                    capabilityState.InstanceId,
                    registration.InstanceId,
                    StringComparison.Ordinal))
            {
                _capabilityStates.Remove(runnerId);
            }
        }
        return new Contract.RunnerDto(
            runnerId,
            registration.Name,
            registration.HostId,
            registration.InstanceId,
            registration.RunnerVersion,
            registration.ProtocolVersion,
            "active",
            registration.RegisteredAt,
            registration.LastSeenAt);
    }

    public Contract.RunnerCapabilitySnapshotDto AdvertiseCapabilities(
        string runnerId,
        Contract.CapabilityAdvertisementRequest request)
    {
        if (request.SchemaVersion != Contract.CapabilityProtocol.CurrentSchemaVersion)
            throw new ArgumentException(
                $"Capability schema {request.SchemaVersion} is unsupported; expected " +
                $"{Contract.CapabilityProtocol.CurrentSchemaVersion}.");
        if (request.FreshForSeconds is < 30 or > 900)
            throw new ArgumentException("Capability freshness must be between 30 and 900 seconds.");
        if (request.Generation <= 0 || request.Capabilities.Count == 0)
            throw new ArgumentException(
                "Capability generation and at least one capability are required.");

        var advertisedAt = request.AdvertisedAt.ToUniversalTime();
        var now = DateTime.UtcNow;
        if (advertisedAt > now.AddMinutes(2))
            throw new ArgumentException("Capability advertisement time is too far in the future.");
        var freshUntil = advertisedAt.AddSeconds(request.FreshForSeconds);
        var capabilities = request.Capabilities
            .Select(capability =>
            {
                var key = capability.Key.Trim().ToLowerInvariant();
                if (key.Length == 0 || string.IsNullOrWhiteSpace(capability.Category))
                    throw new ArgumentException("Capability key and category are required.");
                return new Contract.CapabilityHealthDto(
                    key,
                    capability.Category.Trim().ToLowerInvariant(),
                    capability.Status.Trim().ToLowerInvariant(),
                    Contract.CapabilityHealthStates.Healthy,
                    null,
                    advertisedAt,
                    freshUntil,
                    freshUntil > now,
                    null,
                    null,
                    null,
                    null,
                    0,
                    capability.Version,
                    capability.Identity,
                    capability.Detail,
                    [],
                    []);
            })
            .GroupBy(capability => capability.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(capability => capability.Category, StringComparer.Ordinal)
            .ThenBy(capability => capability.Key, StringComparer.Ordinal)
            .ToArray();

        lock (_gate)
        {
            if (!_registrations.TryGetValue(runnerId, out var registration))
                throw new InvalidOperationException(
                    "Register this review-executor identity before advertising capabilities.");
            if (!string.Equals(registration.InstanceId, request.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Capability advertisement instance does not match the registered review executor.");
            if (_capabilityStates.TryGetValue(runnerId, out var existing)
                && request.Generation < existing.Generation)
            {
                throw new InvalidOperationException(
                    $"Capability generation {request.Generation} is older than {existing.Generation}.");
            }

            registration = registration with { LastSeenAt = now };
            _registrations[runnerId] = registration;
            _capabilityStates[runnerId] = new CapabilityState(
                request.InstanceId,
                request.Generation,
                capabilities,
                request.Telemetry);
            return new Contract.RunnerCapabilitySnapshotDto(
                runnerId,
                registration.Name,
                registration.HostId,
                registration.InstanceId,
                registration.RunnerVersion,
                registration.ProtocolVersion,
                "active",
                registration.RegisteredAt,
                registration.LastSeenAt,
                new Contract.RemoteHostAdmissionDto(
                    registration.HostId,
                    "open",
                    null,
                    null,
                    null,
                    null),
                capabilities,
                request.Telemetry);
        }
    }

    public bool TryGetReviewExecutor(string runnerId, string instanceId, out ReviewExecutor executor)
    {
        if (_registrations.TryGetValue(runnerId, out var registration)
            && string.Equals(registration.InstanceId, instanceId, StringComparison.Ordinal)
            && registration.Capabilities.Contains(Contract.ReviewCapabilities.ReviewExecutor))
        {
            executor = new ReviewExecutor(registration.HostId, registration.Capabilities);
            return true;
        }
        executor = default!;
        return false;
    }

    private sealed record Registration(
        string Name,
        string HostId,
        string InstanceId,
        string RunnerVersion,
        int ProtocolVersion,
        IReadOnlySet<string> Capabilities,
        DateTime RegisteredAt,
        DateTime LastSeenAt);

    private sealed record CapabilityState(
        string InstanceId,
        long Generation,
        IReadOnlyList<Contract.CapabilityHealthDto> Capabilities,
        Contract.HostTelemetrySnapshotDto? Telemetry);

    public sealed record ReviewExecutor(string HostId, IReadOnlySet<string> Capabilities);
}
