using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// The fenced Runner ↔ Server run-lease API under <c>/api/runner/lease</c>
/// (parallel-task-execution.md §8.2C; ADR-0060). The server is the single lease
/// authority: <c>acquire</c> mints a lease id + a monotonic fencing token per
/// task, <c>renew</c>/<c>release</c> must present the current token, and a stale
/// token — presented after a TTL takeover raised the fence — is rejected. That
/// rejection is the split-brain guard.
///
/// <para>
/// The endpoints are thin glue over the unit-tested lease authority
/// (<see cref="RunLeaseService"/>): they validate the task exists, stamp the
/// caller's <see cref="RunnerIdentity"/> onto a partial acquire request, and
/// return the service's <see cref="RunLeaseResponse"/> verbatim. This is the
/// productive successor to the disk-backed <c>.pickup-lock.json</c> lease
/// (ADR-0044, <see cref="PickupLockFile"/>), which stays the same-machine pickup
/// guard until the runner split (ADR-0059) cuts over.
/// </para>
/// </summary>
public static class LeaseEndpoints
{
    private static readonly SemaphoreSlim ClaimGate = new(1, 1);

    public static void MapLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/lease");

        group.MapPost("/acquire", (
            RunLeaseAcquireRequest req,
            ITaskScanner scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            RunLeaseService leases,
            RunnerIdentity identity) =>
        {
            var task = FindTask(scanner, req.TaskKey);
            if (task is null)
                return Results.NotFound(new RunLeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));
            var project = projects.FindByStorageLocation(task.WatchPath)
                          ?? projects.FindByIdOrDisplayName(task.ProjectName);
            var canonical = StampIdentity(req, identity) with
            {
                RepositoryId = string.IsNullOrWhiteSpace(project?.Id) ? task.ProjectName : project.Id,
            };
            return Results.Ok(leases.TryAcquire(canonical));
        });

        group.MapPost("/renew", (RunLeaseHeartbeatRequest req, RunLeaseService leases) =>
            CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                ? Results.Ok(leases.Renew(req))
                : Results.Conflict(new RunLeaseResponse(
                    "Invalid", false, null,
                    "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease renewal.")));

        group.MapPost("/release", (RunLeaseReleaseRequest req, RunLeaseService leases) =>
            CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                ? Results.Ok(leases.Release(req))
                : Results.Conflict(new RunLeaseResponse(
                    "Invalid", false, null,
                    "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease release.")));

        group.MapGet("/{taskKey}", (string taskKey, RunLeaseService leases) =>
            Results.Ok(leases.Peek(taskKey)));

        // Daemon pickup is selected server-side from the project record. The
        // gate makes scan + fenced lease + ready-to-progress move one claim
        // critical section for all remote contenders. The local runner reads
        // the same ExecutionRunner field and therefore never enters this race.
        app.MapPost("/api/runner/claim", async (
            RunnerClaimRequest req,
            TaskScannerService scanner,
            AgentStudio.Projects.ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects,
            TaskTransitionService transitions,
            RunLeaseService leases,
            HttpContext context,
            AgentStudio.Clients.ClientIdentityStore clients,
            AgentStudio.Clients.HostTelemetryStore telemetry,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerClaim");
            if (string.IsNullOrWhiteSpace(req.RunnerId) || string.IsNullOrWhiteSpace(req.RunnerName))
                return Results.BadRequest(new RunnerClaimResponse(RunnerClaimStatus.Invalid, Message: "runnerId and runnerName are required."));

            var clientId = context.Request.Headers["X-Client-Id"].ToString();
            if (req.Telemetry is not null && !string.IsNullOrWhiteSpace(clientId))
                telemetry.Append(clientId, req.Telemetry);
            int? activeSlots = req.Telemetry is null
                ? null
                : Math.Max(0, req.Telemetry.ActiveSlots);
            var client = string.IsNullOrWhiteSpace(clientId)
                ? null
                : clients.RecordRunnerActivity(clientId, activeSlots, req.AvailableSlots, claimed: false);
            if (client?.DrainRequestedAt is not null || client?.Kind == ClientIdentityKind.Retired)
                return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: client.Kind == ClientIdentityKind.Retired
                        ? "runner is retired"
                        : client.RetireRequestedAt is not null
                            ? "runner is draining and will retire after active work finishes"
                            : "runner is draining; no new leases are admitted"));
            if (req.AvailableSlots <= 0)
                return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: "telemetry recorded; no free host slots"));
            if (client is not null && string.Equals(client.RunnerGitStatus, "read-only", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "remote-runner-claim-refused-read-only runner={Runner} clientId={ClientId} detail={Detail}",
                    req.RunnerName, clientId, client.RunnerGitDetail);
                return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: $"runner is read-only: {client.RunnerGitDetail ?? "git push probe failed"}"));
            }

            await ClaimGate.WaitAsync(ct);
            try
            {
                var allWithArchive = scanner.ScanAllJobsWithArchive();
                var waitsOn = TaskReferenceIndex.Build(allWithArchive);
                var eligible = scanner.ScanAllJobs()
                    .Where(t => t.State == TaskStates.Ready)
                    .Where(t =>
                    {
                        var project = settings.Get(t.ProjectName);
                        var assigned = project.ExecutionRunner;
                        return project.RemoteExecutionEnabled
                               && !string.IsNullOrWhiteSpace(assigned)
                               && (string.Equals(assigned, req.RunnerName, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(assigned, req.RunnerId, StringComparison.OrdinalIgnoreCase))
                               && AgentTypes.IsAutoPickupEligible(t.Agent)
                               && !TaskKinds.IsEpic(t.Kind)
                               && !TaskSlugs.IsHumanDecisionNeeded(t.Id)
                               && BuildProfileGate.AllowsAutoPickup(project.BuildProfile)
                               && (!project.IntakeEnabled.GetValueOrDefault()
                                   || t.Phase == LifecyclePhases.IntakePassed)
                               && !waitsOn.EvaluateWaitsOn(t).Blocked;
                    })
                    .OrderBy(t => t.Order)
                    .ThenBy(t => t.CreatedAt);

                TaskInfo? candidate = null;
                RemoteProjectRepository? repository = null;
                foreach (var task in eligible)
                {
                    var registryProject = projects.FindByStorageLocation(task.WatchPath)
                                          ?? projects.FindByIdOrDisplayName(task.ProjectName);
                    repository = RemoteProjectRepositoryResolver.Resolve(
                        registryProject,
                        settings.Get(task.ProjectName).IntegrationBranch);
                    if (repository is not null)
                    {
                        candidate = task;
                        break;
                    }

                    logger.LogInformation(
                        "remote-runner-project-skipped project={Project} task={TaskKey} reason=repository-url-unresolved",
                        task.ProjectName,
                        task.Key ?? task.TaskKey ?? task.Id);
                }

                if (candidate is null || repository is null)
                    return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty));

                var taskKey = candidate.Key ?? candidate.TaskKey;
                if (string.IsNullOrWhiteSpace(taskKey)) taskKey = candidate.Id;
                var acquire = leases.TryAcquire(new RunLeaseAcquireRequest(
                    taskKey, req.RunnerId.Trim(), req.RunnerName.Trim(), req.Hostname,
                    req.Pid, req.BackendName, req.RequestedTtlSeconds,
                    repository.ProjectId,
                    IdempotencyKey: $"claim:{taskKey}:{req.RunnerId.Trim()}:{Guid.NewGuid():N}"));
                if (!acquire.Granted || acquire.Lease is null)
                    return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: acquire.Message ?? acquire.Outcome));

                var move = await transitions.MoveAsync(
                    candidate.Id, TaskStates.Progress, candidate.WatchPath, ct,
                    cause: $"remote-runner:{req.RunnerName.Trim()}");
                if (move.Status != MoveJobStatus.Success)
                {
                    leases.Release(new RunLeaseReleaseRequest(
                        taskKey, acquire.Lease.LeaseId, acquire.Lease.FencingToken, req.RunnerId.Trim(),
                        acquire.Lease.AttemptId, acquire.Lease.AuthorityEpoch,
                        $"claim-rollback:{taskKey}:{acquire.Lease.LeaseId}"));
                    logger.LogWarning(
                        "remote-runner-claim-move-failed project={Project} task={TaskKey} runner={Runner} status={Status} message={Message}",
                        candidate.ProjectName, taskKey, req.RunnerName, move.Status, move.Message);
                    return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: $"claim move refused: {move.Status} {move.Message}"));
                }

                logger.LogInformation(
                    "remote-runner-task-claimed project={Project} projectId={ProjectId} task={TaskKey} runner={Runner} lease={LeaseId} token={FencingToken} repositorySource={RepositorySource} defaultBranch={DefaultBranch}",
                    candidate.ProjectName, repository.ProjectId, taskKey, req.RunnerName, acquire.Lease.LeaseId,
                    acquire.Lease.FencingToken, repository.Source, repository.DefaultBranch);
                if (!string.IsNullOrWhiteSpace(clientId))
                    clients.RecordRunnerActivity(
                        clientId,
                        (activeSlots ?? client?.RunnerActiveSlots ?? 0) + 1,
                        Math.Max(0, req.AvailableSlots - 1),
                        claimed: true);
                return Results.Ok(new RunnerClaimResponse(
                    RunnerClaimStatus.Claimed,
                    taskKey,
                    candidate.Id,
                    candidate.ProjectName,
                    acquire.Lease,
                    ProjectId: repository.ProjectId,
                    RepositoryUrl: repository.RepositoryUrl,
                    DefaultBranch: repository.DefaultBranch));
            }
            finally
            {
                ClaimGate.Release();
            }
        });

        app.MapPost("/api/runner/completion", async (
            RemoteRunCompletionRequest req,
            TaskScannerService scanner,
            TaskTransitionService transitions,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            TimelineLog timeline,
            WorkspaceArtifactCommitService artifactCommits,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var reportedOutcome = req.Outcome ?? string.Empty;
            var task = scanner.ScanAllJobs().FirstOrDefault(t =>
                string.Equals(t.TaskKey, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, req.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (task is null)
                return Results.NotFound(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress, $"No task '{req.TaskKey}'."));

            var outcome = reportedOutcome.Trim().ToLowerInvariant();
            var targetState = outcome switch
            {
                "done" or "noop" => TaskStates.AutoReview,
                "blocked" or "needsinput" or "unknown" => TaskStates.HumanReview,
                _ => string.Empty,
            };
            if (targetState.Length == 0)
                return Results.BadRequest(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Outcome must be Done, NoOp, Blocked, NeedsInput, or Unknown."));

            if (string.IsNullOrWhiteSpace(req.AttemptId)
                || !req.AuthorityEpoch.HasValue
                || string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                return Results.Conflict(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Attempt ID, fence, authority epoch, and idempotency key are required for Remote completion.",
                    RunAttemptId: req.AttemptId,
                    FailureClassification: AttemptWriteStatus.Invalid.ToString()));
            }

            var attemptId = req.AttemptId.Trim();
            var epoch = req.AuthorityEpoch.Value;
            // Result-SHA is independent authority. A salvage commit is useful
            // evidence, but it must never be promoted into the review subject.
            var resultSha = req.ResultSha;
            var completionKey = req.IdempotencyKey.Trim();
            var settled = authority.SettleRun(
                new AttemptWriteReference(attemptId, req.FencingToken, epoch, completionKey),
                outcome,
                resultSha,
                req.Reason,
                req.RunnerId,
                req.LeaseId,
                req.TaskKey);
            if (!settled.Accepted)
            {
                var response = new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, task.State, settled.Message,
                    RunAttemptId: attemptId,
                    FailureClassification: settled.Status.ToString());
                return settled.Status == AttemptWriteStatus.Invalid
                    ? Results.BadRequest(response)
                    : Results.Conflict(response);
            }

            ReviewAttemptDto? reviewAttempt = null;
            if (outcome is "done" or "noop")
            {
                var requirementsPath = Path.Combine(task.FolderPath, "prompt.md");
                var requirements = File.Exists(requirementsPath) ? File.ReadAllText(requirementsPath) : task.Id;
                var run = settled.RunAttempt!;
                var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                    req.TaskKey,
                    run.RepositoryId,
                    run.ResultSha!,
                    run.AttemptId,
                    AttemptAuthorityService.Hash(requirements),
                    AttemptAuthorityService.Hash("remote-review-policy:v1"),
                    run.EvidenceDigests,
                    $"review-subject:{run.AttemptId}:{run.ResultSha}"));
                if (!review.Accepted)
                {
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome, task.State, review.Message,
                        RunAttemptId: run.AttemptId,
                        FailureClassification: review.Status.ToString()));
                }
                reviewAttempt = review.ReviewAttempt;
            }

            if (settled.Status == AttemptWriteStatus.Duplicate
                && string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, targetState, "duplicate delivery",
                    RunAttemptId: attemptId,
                    ReviewAttemptId: reviewAttempt?.AttemptId,
                    ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }

            var source = string.IsNullOrWhiteSpace(req.Source) ? req.RunnerId : req.Source.Trim();
            var details = new Dictionary<string, string>
            {
                ["cli"] = "remote-runner",
                ["status"] = outcome,
                ["runner"] = source,
                ["runAttemptId"] = attemptId,
                ["fence"] = req.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["authorityEpoch"] = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["idempotencyKey"] = completionKey,
                ["sentinel"] = outcome switch
                {
                    "needsinput" => "TASK_NEEDS_INPUT",
                    "unknown" => string.Empty,
                    _ => $"TASK_{outcome.ToUpperInvariant()}",
                },
            };
            if (!string.IsNullOrWhiteSpace(resultSha))
                details["resultSha"] = resultSha;
            if (!string.IsNullOrWhiteSpace(req.SalvageBranch))
                details["salvageBranch"] = req.SalvageBranch;
            if (!string.IsNullOrWhiteSpace(req.SalvageCommitSha))
                details["salvageCommitSha"] = req.SalvageCommitSha;
            if (!string.IsNullOrWhiteSpace(req.SalvageBranchUrl))
                details["salvageBranchUrl"] = req.SalvageBranchUrl;
            if (reviewAttempt is not null)
            {
                details["reviewAttemptId"] = reviewAttempt.AttemptId;
                details["reviewSubjectId"] = reviewAttempt.Subject.SubjectId;
                details["expectedResultSha"] = reviewAttempt.Subject.ExpectedResultSha;
            }
            var gateFile = WriteGateItems(task.FolderPath, req.GateItems);
            if (gateFile is not null)
            {
                details["gateItems"] = string.Join(" | ", req.GateItems!);
                artifactCommits.TryCommitArtifactUpload(null, task.Id, task.FolderPath, [gateFile]);
            }
            if (!string.IsNullOrWhiteSpace(req.SalvageBranch)
                && !string.IsNullOrWhiteSpace(req.SalvageCommitSha))
            {
                var resultsDir = TaskPaths.ResultsDir(task.FolderPath);
                Directory.CreateDirectory(resultsDir);
                var deliverablesPath = Path.Combine(resultsDir, "deliverables.md");
                var branchRef = !string.IsNullOrWhiteSpace(req.SalvageBranchUrl)
                    ? $"[{req.SalvageBranch}]({req.SalvageBranchUrl})"
                    : $"`{req.SalvageBranch}`";
                File.WriteAllText(
                    deliverablesPath,
                    $"# Remote runner deliverables{Environment.NewLine}{Environment.NewLine}" +
                    $"- Salvage branch {branchRef} at `{req.SalvageCommitSha}`.{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
                artifactCommits.TryCommitArtifactUpload(
                    null, task.Id, task.FolderPath, ["results/deliverables.md"]);
            }
            var timelineAlreadyRecorded = timeline.ReadAll(task.FolderPath).Any(evt =>
                evt.Details is not null
                && evt.Details.TryGetValue("idempotencyKey", out var recordedKey)
                && string.Equals(recordedKey, completionKey, StringComparison.Ordinal));
            if (!timelineAlreadyRecorded && !timeline.Append(
                    task.FolderPath,
                    TimelineEventKinds.AgentRunFinished,
                    TimelineActors.Agent,
                    summary: $"remote run {outcome} on {source}",
                    details: details))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Remote completion timeline persistence failed",
                    detail: $"RunAttempt '{attemptId}' is settled, but its idempotent timeline fact was not persisted. Retry the same completion delivery.");
            }

            if (!string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                var move = await transitions.MoveAsync(
                    task.Id, targetState, task.WatchPath, ct,
                    cause: $"remote-runner-completion:{source}");
                if (move.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome, task.State, $"Lane move refused: {move.Status} {move.Message}",
                        RunAttemptId: attemptId,
                        ReviewAttemptId: reviewAttempt?.AttemptId,
                        ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }

            loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogInformation(
                "remote-runner-completion project={Project} task={TaskKey} runner={Runner} outcome={Outcome} targetState={TargetState} token={FencingToken}",
                task.ProjectName, req.TaskKey, source, outcome, targetState, req.FencingToken);
            return Results.Ok(new RemoteRunCompletionResponse(
                req.TaskKey, reportedOutcome, targetState,
                RunAttemptId: attemptId,
                ReviewAttemptId: reviewAttempt?.AttemptId,
                ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
        });
    }

    private static string? WriteGateItems(string folderPath, IReadOnlyList<string>? gateItems)
    {
        var items = (gateItems ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Replace('\r', ' ').Replace('\n', ' ').Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0) return null;

        const string relativePath = "orchestrator-follow-up.md";
        var path = Path.Combine(folderPath, relativePath);
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var sb = new StringBuilder(existing);
        if (sb.Length == 0)
            sb.Append("# Orchestrator follow-up\n\n");
        else if (!existing.EndsWith("\n\n", StringComparison.Ordinal))
            sb.Append(existing.EndsWith('\n') ? "\n" : "\n\n");
        foreach (var item in items)
        {
            var row = $"- [ ] {item}";
            if (!existing.Contains(row, StringComparison.Ordinal))
                sb.Append(row).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return relativePath;
    }

    private static bool CanonicalLeaseWritePresent(
        string? attemptId,
        long? authorityEpoch,
        string? idempotencyKey) =>
        !string.IsNullOrWhiteSpace(attemptId)
        && authorityEpoch is > 0
        && !string.IsNullOrWhiteSpace(idempotencyKey);

    /// <summary>
    /// Fill a partial acquire request with this backend's runner identity so a
    /// local caller need only name the task; a remote runner supplies its own
    /// identity and those values win. Keeps the previously-unused lease API
    /// productive for the in-process runner without forcing every caller to
    /// re-derive host/pid/backend.
    /// </summary>
    private static RunLeaseAcquireRequest StampIdentity(RunLeaseAcquireRequest req, RunnerIdentity identity) => req with
    {
        RunnerId = string.IsNullOrWhiteSpace(req.RunnerId) ? identity.RunnerId : req.RunnerId,
        RunnerName = string.IsNullOrWhiteSpace(req.RunnerName) ? identity.RunnerName : req.RunnerName,
        Hostname = string.IsNullOrWhiteSpace(req.Hostname) ? identity.Hostname : req.Hostname,
        BackendName = string.IsNullOrWhiteSpace(req.BackendName) ? identity.BackendName : req.BackendName,
        Pid = req.Pid == 0 ? Environment.ProcessId : req.Pid,
    };

    private static TaskInfo? FindTask(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        return scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase));
    }
}
