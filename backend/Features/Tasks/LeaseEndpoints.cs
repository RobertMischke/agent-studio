using AgentStudio.Pipeline;
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

        group.MapPost("/acquire", async (
            RunLeaseAcquireRequest req,
            HttpContext context,
            ITaskScanner scanner,
            ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects,
            RunLeaseService leases,
            RunnerIdentity identity,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName)) return Results.Unauthorized();
            await ClaimGate.WaitAsync(ct);
            try
            {
                var task = FindTask(scanner, req.TaskKey);
                if (task is null)
                    return Results.NotFound(new RunLeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));
                if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal)
                {
                    var settingsProject = settings.Get(task.ProjectName);
                    if (!ProjectExecutionPolicy.IsAssignedRemote(
                            settingsProject, principal.RunnerId, principal.RunnerName))
                        return Results.Json(new RunLeaseResponse(
                            "ProjectDenied", false, null,
                            "The Runner is not assigned to this project's execution location."), statusCode: StatusCodes.Status403Forbidden);
                }
                var project = projects.FindByStorageLocation(task.WatchPath)
                              ?? projects.FindByIdOrDisplayName(task.ProjectName);
                var repository = RemoteProjectRepositoryResolver.Resolve(
                    project,
                    settings.Get(task.ProjectName).IntegrationBranch);
                var clientId = context.Items["ClientId"] as string
                               ?? context.Request.Headers["X-Client-Id"].ToString();
                var canonical = StampIdentity(req, identity) with
                {
                    RepositoryId = repository?.RepositoryId
                                   ?? (string.IsNullOrWhiteSpace(project?.Id) ? task.ProjectName : project.Id),
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                };
                return Results.Ok(leases.TryAcquire(canonical));
            }
            finally
            {
                ClaimGate.Release();
            }
        });

        group.MapPost("/renew", (RunLeaseHeartbeatRequest req, HttpContext context, RunLeaseService leases) =>
            !RunnerMatches(context, req.RunnerId)
                ? Results.Unauthorized()
                : CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                    ? Results.Ok(leases.Renew(req))
                    : Results.Conflict(new RunLeaseResponse(
                        "Invalid", false, null,
                        "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease renewal.")));

        group.MapPost("/release", (RunLeaseReleaseRequest req, HttpContext context, RunLeaseService leases) =>
            !RunnerMatches(context, req.RunnerId)
                ? Results.Unauthorized()
                : CanonicalLeaseWritePresent(req.AttemptId, req.AuthorityEpoch, req.IdempotencyKey)
                    ? Results.Ok(leases.Release(req))
                    : Results.Conflict(new RunLeaseResponse(
                        "Invalid", false, null,
                        "AttemptId, AuthorityEpoch, and IdempotencyKey are required for lease release.")));

        group.MapGet("/{taskKey}", (string taskKey, RunLeaseService leases) =>
            Results.Ok(leases.Peek(taskKey)));

        // Interactive project-chat work uses the same remote pull direction as
        // card runs. Studio queues an opaque request for the project's assigned
        // runner; the host claims, renews and completes it with a claim token.
        // No central process reaches into the runner over SSH.
        app.MapPost("/api/runner/project-chat/claim",
            (RemoteChatWorkClaimRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId, req.RunnerName))
                    return Results.Unauthorized();
                return Results.Ok(broker.TryClaim(req));
            });

        app.MapPost("/api/runner/project-chat/renew",
            (RemoteChatWorkRenewRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId))
                    return Results.Unauthorized();
                return broker.Renew(req)
                    ? Results.Ok(new { renewed = true })
                    : Results.Conflict(new { renewed = false, error = "stale project-chat claim" });
            });

        app.MapPost("/api/runner/project-chat/complete",
            (RemoteChatWorkCompletionRequest req, HttpContext context, RemoteChatWorkBroker broker) =>
            {
                if (!RunnerMatches(context, req.RunnerId))
                    return Results.Unauthorized();
                return broker.Complete(req)
                    ? Results.Ok(new { accepted = true })
                    : Results.Conflict(new { accepted = false, error = "stale project-chat claim" });
            });

        // Daemon pickup is selected server-side from the project record. The
        // gate makes scan + fenced lease + ready-to-progress move one claim
        // critical section for all remote contenders. The local runner reads
        // the same resolved executionLocation and therefore never enters this race.
        app.MapPost("/api/runner/claim", async (
            RunnerClaimRequest req,
            TaskScannerService scanner,
            AgentStudio.Projects.ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects,
            TaskTransitionService transitions,
            RunLeaseService leases,
            TaskSessionLog sessions,
            HttpContext context,
            AgentStudio.Clients.ClientIdentityStore clients,
            AgentStudio.Clients.HostTelemetryStore telemetry,
            AccessSecurityStore accessSecurity,
            HumanReviewEscalation humanReviewEscalation,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerClaim");
            var remoteClaimFailures = new RemoteClaimFailureBudget(
                loggerFactory.CreateLogger<RemoteClaimFailureBudget>());
            if (string.IsNullOrWhiteSpace(req.RunnerId) || string.IsNullOrWhiteSpace(req.RunnerName))
                return Results.BadRequest(new RunnerClaimResponse(RunnerClaimStatus.Invalid, Message: "runnerId and runnerName are required."));
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName))
                return Results.Unauthorized();

            var runnerPrincipal = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
            var clientId = context.Items["ClientId"] as string ?? context.Request.Headers["X-Client-Id"].ToString();
            if (req.Telemetry is not null && !string.IsNullOrWhiteSpace(clientId))
                telemetry.Append(clientId, req.Telemetry);
            int? activeSlots = req.ActiveSlots is not null
                ? Math.Max(0, req.ActiveSlots.Value)
                : req.Telemetry is null
                    ? null
                    : Math.Max(0, req.Telemetry.ActiveSlots);
            var securedRunner = runnerPrincipal is null
                ? null
                : accessSecurity.RecordRunnerActivity(
                    runnerPrincipal.RunnerId, activeSlots, req.AvailableSlots, claimed: false);
            var client = string.IsNullOrWhiteSpace(clientId)
                ? null
                : clients.RecordRunnerActivity(clientId, activeSlots, req.AvailableSlots, claimed: false);
            if (securedRunner is not null && !accessSecurity.RunnerAcceptsClaims(securedRunner.Id))
                return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: securedRunner.RetiredAt is not null
                        ? "runner is retired"
                        : securedRunner.RetireRequestedAt is not null
                            ? "runner is draining and will retire after active work finishes"
                            : "runner is draining; no new leases are admitted"));
            if (client?.DrainRequestedAt is not null || client?.Kind == ClientIdentityKind.Retired)
                return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty,
                    Message: client.Kind == ClientIdentityKind.Retired
                        ? "runner is retired"
                        : client.RetireRequestedAt is not null
                            ? "runner is draining and will retire after active work finishes"
                            : "runner is draining; no new leases are admitted"));
            await ClaimGate.WaitAsync(ct);
            try
            {
                // A successful claim has already moved its selected card to
                // Progress, so replay must consult durable acquire authority
                // before Ready-task selection. Any known delivery, including a
                // stale or superseded one, is terminal for this request and
                // must never fall through to claim unrelated work.
                var requestedClaimKey = req.IdempotencyKey?.Trim();
                if (!string.IsNullOrWhiteSpace(requestedClaimKey))
                {
                    var replay = leases.TryReplayAcquire(
                        req.RunnerId.Trim(), requestedClaimKey);
                    if (replay is not null)
                    {
                        if (!replay.Granted || replay.Lease is null)
                        {
                            return Results.Ok(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: replay.Message ?? replay.Outcome));
                        }

                        var replayedTask = FindTask(scanner, replay.Lease.TaskKey);
                        if (replayedTask is null)
                        {
                            return Results.Ok(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: "The original claim task is no longer available."));
                        }
                        var replayedProject = projects.FindByStorageLocation(replayedTask.WatchPath)
                                              ?? projects.FindByIdOrDisplayName(replayedTask.ProjectName);
                        var replayedRepository = RemoteProjectRepositoryResolver.Resolve(
                            replayedProject,
                            settings.Get(replayedTask.ProjectName).IntegrationBranch);
                        if (replayedRepository is null)
                        {
                            return Results.Ok(new RunnerClaimResponse(
                                RunnerClaimStatus.Empty,
                                Message: "The original claim repository is no longer configured."));
                        }

                        if (runnerPrincipal is not null)
                            accessSecurity.RecordRunnerActivity(
                                runnerPrincipal.RunnerId,
                                (activeSlots ?? securedRunner?.ActiveSlots ?? 0) + 1,
                                Math.Max(0, req.AvailableSlots - 1),
                                claimed: true);
                        if (!string.IsNullOrWhiteSpace(clientId))
                            clients.RecordRunnerActivity(
                                clientId,
                                (activeSlots ?? client?.RunnerActiveSlots ?? 0) + 1,
                                Math.Max(0, req.AvailableSlots - 1),
                                claimed: true);
                        return Results.Ok(new RunnerClaimResponse(
                            RunnerClaimStatus.Claimed,
                            replay.Lease.TaskKey,
                            replayedTask.Id,
                            replayedTask.ProjectName,
                            replay.Lease,
                            ProjectId: replayedRepository.ProjectId,
                            RepositoryUrl: replayedRepository.RepositoryUrl,
                            DefaultBranch: replayedRepository.DefaultBranch,
                            TaskKind: replayedTask.Kind));
                    }
                }

                var recoveredSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var activeTaskKeys = req.ActiveTaskKeys is null
                    ? null
                    : req.ActiveTaskKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requeueGraceSeconds = Math.Clamp(
                    configuration.GetValue("Runner:RemoteRequeue:GraceSeconds", 120),
                    1,
                    900);
                var now = DateTime.UtcNow;

                // A daemon or server restart may leave the card in Progress while
                // its original CLI still runs. A free lease is therefore not
                // enough to requeue: wait through the authority grace and require
                // this assigned runner poll to answer that the task is absent
                // from its active process set.
                foreach (var interrupted in scanner.ScanAllJobs().Where(t => t.State == TaskStates.Progress))
                {
                    var project = settings.Get(interrupted.ProjectName);
                    if (!ProjectExecutionPolicy.AllowsAutomaticPickup(project)
                        || !ProjectExecutionPolicy.IsAssignedRemote(project, req.RunnerId, req.RunnerName))
                        continue;
                    var interruptedKey = interrupted.Key ?? interrupted.TaskKey ?? interrupted.Id;
                    if (leases.Peek(interruptedKey).Outcome != "Free") continue;
                    var inspection = leases.Inspect(interruptedKey);
                    var lastAuthorityActivity = inspection.Lease?.LastHeartbeatAt
                                                ?? interrupted.EnteredLaneAt;
                    var requeueDecision = RemoteRunRequeuePolicy.Decide(
                        new RemoteRunRequeueFacts(
                            Math.Max(0, (now - lastAuthorityActivity.ToUniversalTime()).TotalSeconds),
                            requeueGraceSeconds,
                            RunnerRespondedWithActiveSet: activeTaskKeys is not null,
                            RunnerReportsTaskActive: activeTaskKeys?.Contains(interruptedKey) == true));
                    if (requeueDecision.Action != RemoteRunRequeueAction.Requeue)
                    {
                        logger.LogInformation(
                            "remote-runner-requeue-deferred task={TaskKey} runner={Runner} reason={Reason} detail={Detail}",
                            interruptedKey, req.RunnerName, requeueDecision.ReasonCode, requeueDecision.Detail);
                        continue;
                    }
                    var recoveryWrite = leases.CurrentWriteReference(
                        interruptedKey,
                        $"lane-recovery:{interruptedKey}:{req.RunnerId.Trim()}");
                    var preparationFailure = remoteClaimFailures.GetState(interrupted);
                    if (preparationFailure?.Attempts >= RemoteClaimFailureBudget.MaxAttempts)
                    {
                        var reason =
                            $"Remote claim repository/environment preparation failed " +
                            $"({preparationFailure.Attempts}/{RemoteClaimFailureBudget.MaxAttempts}): " +
                            preparationFailure.Reason;
                        var escalated = await humanReviewEscalation.EscalateAsync(
                            interrupted.Id,
                            interrupted.WatchPath,
                            interrupted.ProjectName,
                            HumanReviewEscalationCategories.RemoteClaimEnvironment,
                            reason,
                            ct,
                            recoveryWrite);
                        logger.Log(
                            escalated.Status == MoveJobStatus.Success
                                ? LogLevel.Error
                                : LogLevel.Critical,
                            "remote-claim-exhausted-recovery project={Project} task={TaskKey} status={Status} reason={Reason}",
                            interrupted.ProjectName,
                            interruptedKey,
                            escalated.Status,
                            reason);
                        continue;
                    }
                    await transitions.MoveAsync(
                        interrupted.Id, TaskStates.Ready, interrupted.WatchPath, ct,
                        cause: $"remote-runner-lease-recovery:{req.RunnerName.Trim()}",
                        authorityWrite: recoveryWrite,
                        suppressProductExecution: true);
                    if (recoveryWrite is not null)
                        recoveredSources[interruptedKey] = recoveryWrite.AttemptId;
                }

                var claimSnapshot = scanner.GetLiveSnapshotWithReferenceIndex();
                var liveSnapshot = claimSnapshot.Live;
                var waitsOn = claimSnapshot.References;

                if (req.AvailableSlots <= 0)
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty,
                        Message: "runner status recorded; no free host slots"));

                var eligible = liveSnapshot
                    .Where(t => t.State == TaskStates.Ready)
                    .Where(t =>
                    {
                        var project = settings.Get(t.ProjectName);
                        return ProjectExecutionPolicy.AllowsAutomaticPickup(project)
                               && ProjectExecutionPolicy.IsAssignedRemote(project, req.RunnerId, req.RunnerName)
                               && AgentTypes.IsAutoPickupEligible(t.Agent)
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
                TaskInfo? failedPreflightCandidate = null;
                RemoteProjectRepository? failedPreflightRepository = null;
                RunnerProjectPreflight? failedProjectPreflight = null;
                var readOnlyCodingSkipped = false;
                string? nonRemoteCapableProject = null;
                foreach (var task in eligible)
                {
                    if (client is not null
                        && string.Equals(client.RunnerGitStatus, "read-only", StringComparison.OrdinalIgnoreCase)
                        && !TaskKinds.IsEpic(task.Kind))
                    {
                        readOnlyCodingSkipped = true;
                        logger.LogWarning(
                            "remote-runner-coding-claim-refused-read-only runner={Runner} clientId={ClientId} task={TaskKey} detail={Detail}",
                            req.RunnerName, clientId, task.Key ?? task.Id, client.RunnerGitDetail);
                        continue;
                    }
                    var registryProject = projects.FindByStorageLocation(task.WatchPath)
                                          ?? projects.FindByIdOrDisplayName(task.ProjectName);
                    repository = RemoteProjectRepositoryResolver.Resolve(
                        registryProject,
                        settings.Get(task.ProjectName).IntegrationBranch);
                    if (repository is not null)
                    {
                        var cached = string.IsNullOrWhiteSpace(clientId)
                            ? null
                            : clients.FindRunnerProjectPreflight(clientId, repository.ProjectId);
                        if (cached is not null
                            && string.Equals(cached.RegistrationFingerprint,
                                ProjectDeliveryPreflightFingerprint.Create(repository), StringComparison.Ordinal)
                            && !string.Equals(cached.Status, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            failedPreflightCandidate ??= task;
                            failedPreflightRepository ??= repository;
                            failedProjectPreflight ??= cached;
                            repository = null;
                            continue;
                        }
                        candidate = task;
                        break;
                    }

                    nonRemoteCapableProject = task.ProjectName;
                    logger.LogWarning(
                        "remote-runner-project-not-remote-capable project={Project} task={TaskKey} reason=repository-url-not-configured",
                        task.ProjectName,
                        task.Key ?? task.TaskKey ?? task.Id);
                }

                if ((candidate is null || repository is null)
                    && failedPreflightCandidate is not null
                    && failedPreflightRepository is not null
                    && failedProjectPreflight is not null)
                {
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightFailed,
                        ProjectName: failedPreflightCandidate.ProjectName,
                        Message: $"Project delivery preflight failed: {failedProjectPreflight.Detail}",
                        ProjectId: failedPreflightRepository.ProjectId,
                        RepositoryUrl: failedPreflightRepository.RepositoryUrl,
                        DefaultBranch: failedPreflightRepository.DefaultBranch,
                        TaskKind: failedPreflightCandidate.Kind,
                        RegistrationFingerprint: ProjectDeliveryPreflightFingerprint.Create(failedPreflightRepository)));
                }

                if (candidate is null || repository is null)
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.Empty,
                        Message: readOnlyCodingSkipped
                            ? $"runner is read-only: {client?.RunnerGitDetail ?? "git push probe failed"}"
                            : nonRemoteCapableProject is not null
                                ? $"project '{nonRemoteCapableProject}' is not remote-capable: repository URL is not configured"
                                : null));

                if (string.IsNullOrWhiteSpace(clientId))
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.Invalid,
                        Message: "A registered host client identity is required for project delivery preflight."));

                var registrationFingerprint = ProjectDeliveryPreflightFingerprint.Create(repository);
                if (req.ProjectPreflight is not null)
                {
                    if (!string.Equals(req.ProjectPreflight.ProjectId, repository.ProjectId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(req.ProjectPreflight.RegistrationFingerprint, registrationFingerprint, StringComparison.Ordinal))
                    {
                        return Results.Ok(new RunnerClaimResponse(
                            RunnerClaimStatus.Invalid,
                            ProjectName: candidate.ProjectName,
                            Message: "The project registration changed while delivery preflight was running. Retry the claim.",
                            ProjectId: repository.ProjectId,
                            RepositoryUrl: repository.RepositoryUrl,
                            DefaultBranch: repository.DefaultBranch,
                            RegistrationFingerprint: registrationFingerprint));
                    }

                    var urlsMatch = SameRepositoryUrl(req.ProjectPreflight.FetchUrl, repository.RepositoryUrl)
                                    && SameRepositoryUrl(req.ProjectPreflight.PushUrl, repository.RepositoryUrl);
                    var succeeded = req.ProjectPreflight.Succeeded && urlsMatch;
                    var detail = urlsMatch
                        ? OneLine(req.ProjectPreflight.Detail)
                        : $"fetch and push URL must both match registered repository '{repository.RepositoryUrl}'";
                    clients.SetRunnerProjectPreflight(clientId, new RunnerProjectPreflight
                    {
                        ProjectId = repository.ProjectId,
                        ProjectName = candidate.ProjectName,
                        RegistrationFingerprint = registrationFingerprint,
                        RepositoryUrl = repository.RepositoryUrl,
                        FetchUrl = req.ProjectPreflight.FetchUrl?.Trim() ?? "",
                        PushUrl = req.ProjectPreflight.PushUrl?.Trim() ?? "",
                        Status = succeeded ? "ready" : "failed",
                        Detail = detail,
                        CheckedAt = req.ProjectPreflight.CheckedAt.ToUniversalTime(),
                    });
                    logger.Log(succeeded ? LogLevel.Information : LogLevel.Warning,
                        "remote-runner-project-preflight project={Project} projectId={ProjectId} runner={Runner} status={Status} detail={Detail}",
                        candidate.ProjectName, repository.ProjectId, req.RunnerName, succeeded ? "ready" : "failed", detail);
                }

                var projectPreflight = clients.FindRunnerProjectPreflight(clientId, repository.ProjectId);
                if (projectPreflight is null
                    || !string.Equals(projectPreflight.RegistrationFingerprint, registrationFingerprint, StringComparison.Ordinal))
                {
                    if (projectPreflight is not null)
                        clients.InvalidateRunnerProjectPreflights(repository.ProjectId);
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightRequired,
                        ProjectName: candidate.ProjectName,
                        Message: "Project delivery preflight is required before the first claim.",
                        ProjectId: repository.ProjectId,
                        RepositoryUrl: repository.RepositoryUrl,
                        DefaultBranch: repository.DefaultBranch,
                        TaskKind: candidate.Kind,
                        RegistrationFingerprint: registrationFingerprint));
                }

                if (!string.Equals(projectPreflight.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(new RunnerClaimResponse(
                        RunnerClaimStatus.PreflightFailed,
                        ProjectName: candidate.ProjectName,
                        Message: $"Project delivery preflight failed: {projectPreflight.Detail}",
                        ProjectId: repository.ProjectId,
                        RepositoryUrl: repository.RepositoryUrl,
                        DefaultBranch: repository.DefaultBranch,
                        TaskKind: candidate.Kind,
                        RegistrationFingerprint: registrationFingerprint));
                }

                var taskKey = candidate.Key ?? candidate.TaskKey;
                if (string.IsNullOrWhiteSpace(taskKey)) taskKey = candidate.Id;
                remoteClaimFailures.PrepareForClaim(candidate);
                var claimKey = string.IsNullOrWhiteSpace(req.IdempotencyKey)
                    ? $"claim:{taskKey}:{req.RunnerId.Trim()}:{Guid.NewGuid():N}"
                    : req.IdempotencyKey.Trim();
                var acquire = leases.TryAcquire(new RunLeaseAcquireRequest(
                    taskKey, req.RunnerId.Trim(), req.RunnerName.Trim(), req.Hostname,
                    req.Pid, req.BackendName, req.RequestedTtlSeconds,
                    repository.RepositoryId,
                    SourceRunAttemptId: recoveredSources.GetValueOrDefault(taskKey),
                    IdempotencyKey: claimKey)
                {
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                });
                if (!acquire.Granted || acquire.Lease is null)
                    return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: acquire.Message ?? acquire.Outcome));

                var move = await transitions.MoveAsync(
                    candidate.Id, TaskStates.Progress, candidate.WatchPath, ct,
                    cause: $"remote-runner:{req.RunnerName.Trim()}",
                    authorityWrite: new AttemptWriteReference(
                        acquire.Lease.AttemptId!,
                        acquire.Lease.FencingToken,
                        acquire.Lease.AuthorityEpoch,
                        $"lane-claim:{claimKey}"));
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
                sessions.AppendSessionEvent(candidate.Id, new SessionEvent
                {
                    Ts = acquire.Lease.AcquiredAt,
                    Kind = "start",
                    Cli = "remote-runner",
                    Model = candidate.Model,
                    ThinkingLevel = candidate.ThinkingLevel,
                    Cwd = candidate.FolderPath,
                    ExecutionLocation = new TaskExecutionLocation
                    {
                        State = TaskExecutionStates.RemoteRunning,
                        ExecutionKind = "remote",
                        RunnerId = acquire.Lease.RunnerId,
                        ClientId = acquire.Lease.ClientId ?? acquire.Lease.RunnerId,
                        HostDisplayName = string.IsNullOrWhiteSpace(acquire.Lease.RunnerName) ? acquire.Lease.Hostname : acquire.Lease.RunnerName,
                        ConfiguredRunnerId = ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(candidate.ProjectName)),
                        StartedAt = acquire.Lease.AcquiredAt,
                        LastHeartbeat = acquire.Lease.LastHeartbeatAt,
                        LastActivityAt = acquire.Lease.LastHeartbeatAt,
                        ProcessId = acquire.Lease.Pid > 0 ? acquire.Lease.Pid : null,
                        Branch = candidate.Provenance?.Branch,
                        WorktreePath = candidate.FolderPath,
                        ConnectionState = "connected",
                        LeaseState = "active",
                        TrustReason = "Captured from the fenced run lease granted by the task server.",
                    },
                }, candidate.WatchPath);
                if (runnerPrincipal is not null)
                    accessSecurity.RecordRunnerActivity(
                        runnerPrincipal.RunnerId,
                        (activeSlots ?? securedRunner?.ActiveSlots ?? 0) + 1,
                        Math.Max(0, req.AvailableSlots - 1),
                        claimed: true);
                if (!string.IsNullOrWhiteSpace(clientId))
                    clients.RecordRunnerActivity(
                        clientId,
                        (activeSlots ?? client?.RunnerActiveSlots ?? 0) + 1,
                        Math.Max(0, req.AvailableSlots - 1),
                        claimed: true);
                if (runnerPrincipal is not null)
                {
                    accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                        DateTime.UtcNow, "claim", taskKey, candidate.ProjectName,
                        InitiatingPrincipal(candidate.OwnerClientId), runnerPrincipal.RunnerId, runnerPrincipal.CredentialId,
                        acquire.Lease.FencingToken));
                }
                return Results.Ok(new RunnerClaimResponse(
                    RunnerClaimStatus.Claimed,
                    taskKey,
                    candidate.Id,
                    candidate.ProjectName,
                    acquire.Lease,
                    ProjectId: repository.ProjectId,
                    RepositoryUrl: repository.RepositoryUrl,
                    DefaultBranch: repository.DefaultBranch,
                    TaskKind: candidate.Kind));
            }
            finally
            {
                ClaimGate.Release();
            }
        });

        app.MapPost("/api/runner/epic-planning-prompt", (
            RemoteEpicPlanningPromptRequest req,
            TaskScannerService scanner,
            RunLeaseService leases,
            RuntimePromptService prompts,
            AgentStudio.Projects.ProjectSettingsService settings) =>
        {
            if (!leases.IsCurrent(req.TaskKey, req.LeaseId, req.FencingToken, req.RunnerId))
                return Results.Conflict(new { error = "Lease id, fencing token, or runner id does not match the current holder." });
            var epic = scanner.ScanAllJobs().FirstOrDefault(t =>
                string.Equals(t.TaskKey, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, req.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (epic is null) return Results.NotFound();
            if (!TaskKinds.IsEpic(epic.Kind))
                return Results.BadRequest(new { error = "The claimed task is not an Epic planning run." });

            var goal = File.Exists(Path.Combine(epic.FolderPath, "prompt.md"))
                ? File.ReadAllText(Path.Combine(epic.FolderPath, "prompt.md"))
                : string.Empty;
            var rendered = prompts.Render(RuntimePromptService.EpicDecomposition, new Dictionary<string, string?>
            {
                ["title"] = string.IsNullOrWhiteSpace(epic.Title) ? "(untitled)" : epic.Title,
                ["prompt_text"] = goal,
                ["working_directory"] = req.WorkingDirectory,
                ["repository_path"] = req.WorkingDirectory,
                ["job_folder"] = "server-managed task folder",
                ["prompt_path"] = "prompt.md via the runner API",
                ["attachments_list"] = "",
                ["mode_framing"] = prompts.RenderModeFraming(TaskModes.Planning, epic.AllowWebAccess),
            });
            var project = settings.Get(epic.ProjectName);
            return Results.Ok(new RemoteEpicPlanningPromptResponse(
                rendered,
                epic.CliType,
                project.EpicPlanningModel ?? epic.Model,
                project.EpicPlanningThinkingLevel ?? epic.ThinkingLevel));
        });

        app.MapPost("/api/runner/completion", async (
            RemoteRunCompletionRequest req,
            HttpContext context,
            TaskScannerService scanner,
            TaskTransitionService transitions,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            TimelineLog timeline,
            AccessSecurityStore accessSecurity,
            WorkspaceArtifactCommitService artifactCommits,
            AgentStudio.Projects.ProjectSettingsService projectSettings,
            TaskMutationService mutations,
            TaskStateMachine states,
            OrchestratorChatLog chatLog,
            HumanReviewEscalation humanReviewEscalation,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, req.RunnerId)) return Results.Unauthorized();
            await ClaimGate.WaitAsync(ct);
            try
            {
            var remoteClaimFailures = new RemoteClaimFailureBudget(
                loggerFactory.CreateLogger<RemoteClaimFailureBudget>());
            var reportedOutcome = req.Outcome ?? string.Empty;
            var task = scanner.ScanAllJobs().FirstOrDefault(t =>
                string.Equals(t.TaskKey, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, req.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Key, req.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (task is null)
                return Results.NotFound(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress, $"No task '{req.TaskKey}'."));

            var outcome = reportedOutcome.Trim().ToLowerInvariant();
            var isEpicPlanning = TaskKinds.IsEpic(task.Kind);
            var targetState = outcome switch
            {
                "done" or "noop" => TaskStates.AutoReview,
                "blocked" or "needsinput" or "unknown" => TaskStates.Escalated,
                "environmentfailure" => TaskStates.Ready,
                _ => string.Empty,
            };
            if (targetState.Length == 0)
                return Results.BadRequest(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Outcome must be Done, NoOp, Blocked, NeedsInput, Unknown, or EnvironmentFailure."));

            // AGT-2178: Epic planning is source-read-only - it produces no commit
            // and therefore no fenced ResultSha. The 2177 ResultSha gate only
            // applies to coding completions; epic planning is finalized through
            // the isEpicPlanning branch below (which needs no ResultSha).
            if (targetState == TaskStates.AutoReview
                && !isEpicPlanning
                && (!ReviewSubjectStore.IsValidResultSha(req.ResultSha)
                    || string.IsNullOrWhiteSpace(req.AttemptChainId)
                    || !string.Equals(req.AttemptChainId, req.LeaseId, StringComparison.Ordinal)))
            {
                return Results.BadRequest(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Done/NoOp completion requires a full fenced ResultSha and AttemptChainId equal to the current lease id.",
                    RunAttemptId: req.AttemptId,
                    FailureClassification: AttemptWriteStatus.Invalid.ToString()));
            }

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
            AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope? resultEnvelope = null;
            string? resultEnvelopeDigest = null;
            var leasedRun = authority.GetRun(attemptId);
            if (!isEpicPlanning
                && resultSha is not null
                && leasedRun is not null
                && !string.IsNullOrWhiteSpace(req.BaseSha)
                && !string.IsNullOrWhiteSpace(req.ImmutableResultRef)
                && !string.IsNullOrWhiteSpace(req.ArtifactManifestDigest))
            {
                resultEnvelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
                    leasedRun.RepositoryId,
                    attemptId,
                    req.BaseSha,
                    resultSha,
                    req.ImmutableResultRef,
                    null,
                    req.ArtifactManifestDigest,
                    RepositoryUrl: req.Repository);
                resultEnvelopeDigest =
                    AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(resultEnvelope);
            }
            else if (!isEpicPlanning && outcome is "done" or "noop")
            {
                // Without the envelope trio the review subject can never be
                // materialised and the card will terminalize as
                // SnapshotUnavailable after the grace window. Surface the
                // drift loudly at ingest instead of failing silently later.
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                    "Completion for {TaskKey} (attempt {AttemptId}, runner {RunnerId}) carries no result-envelope trio "
                    + "(BaseSha={HasBaseSha}, ImmutableResultRef={HasResultRef}, ArtifactManifestDigest={HasManifestDigest}, run-known={RunKnown}); "
                    + "auto-review cannot materialise this subject - update the runner binary to one that emits the fields.",
                    req.TaskKey, attemptId, req.RunnerId,
                    !string.IsNullOrWhiteSpace(req.BaseSha),
                    !string.IsNullOrWhiteSpace(req.ImmutableResultRef),
                    !string.IsNullOrWhiteSpace(req.ArtifactManifestDigest),
                    leasedRun is not null);
            }
            var settled = authority.SettleRun(
                new AttemptWriteReference(attemptId, req.FencingToken, epoch, completionKey),
                outcome,
                resultSha,
                req.Reason,
                req.RunnerId,
                req.LeaseId,
                req.TaskKey,
                requireResultSha: !isEpicPlanning && outcome is ("done" or "noop"),
                resultEnvelope: resultEnvelope,
                resultEnvelopeDigest: resultEnvelopeDigest);
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
            if (!isEpicPlanning && outcome is ("done" or "noop"))
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

            if (!isEpicPlanning
                && settled.Status == AttemptWriteStatus.Duplicate
                && string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, targetState, "duplicate delivery",
                    RunAttemptId: attemptId,
                    ReviewAttemptId: reviewAttempt?.AttemptId,
                    ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }

            var source = CredentialRedactor.Redact(
                string.IsNullOrWhiteSpace(req.Source) ? req.RunnerId : req.Source.Trim());
            var salvageBranch = CredentialRedactor.Redact(req.SalvageBranch);
            var salvageCommitSha = CredentialRedactor.Redact(req.SalvageCommitSha);
            var salvageBranchUrl = CredentialRedactor.Redact(req.SalvageBranchUrl);
            var salvageRecoveryBranchUrl = CredentialRedactor.Redact(req.SalvageRecoveryBranchUrl);
            var reportedReason = CredentialRedactor.Redact(req.Reason);
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
            if (!string.IsNullOrWhiteSpace(salvageBranch))
                details["salvageBranch"] = salvageBranch;
            if (!string.IsNullOrWhiteSpace(salvageCommitSha))
                details["salvageCommitSha"] = salvageCommitSha;
            if (!string.IsNullOrWhiteSpace(salvageBranchUrl))
                details["salvageBranchUrl"] = salvageBranchUrl;
            if (!string.IsNullOrWhiteSpace(req.ResultSha))
                details["resultSha"] = req.ResultSha;
            if (!string.IsNullOrWhiteSpace(req.AttemptChainId))
                details["attemptChainId"] = req.AttemptChainId;
            if (!string.IsNullOrWhiteSpace(req.SalvageResolution))
                details["salvageResolution"] = req.SalvageResolution;
            if (!string.IsNullOrWhiteSpace(req.SalvageLocalCommitSha))
                details["salvageLocalCommitSha"] = req.SalvageLocalCommitSha;
            if (!string.IsNullOrWhiteSpace(req.SalvageRecoveryBranch))
                details["salvageRecoveryBranch"] = req.SalvageRecoveryBranch;
            if (!string.IsNullOrWhiteSpace(req.SalvageRecoveryCommitSha))
                details["salvageRecoveryCommitSha"] = req.SalvageRecoveryCommitSha;
            if (!string.IsNullOrWhiteSpace(salvageRecoveryBranchUrl))
                details["salvageRecoveryBranchUrl"] = salvageRecoveryBranchUrl;
            if (!string.IsNullOrWhiteSpace(req.SalvageAuthoritativeBaseBranch))
                details["salvageAuthoritativeBaseBranch"] = req.SalvageAuthoritativeBaseBranch;
            if (!string.IsNullOrWhiteSpace(req.SalvageAuthoritativeBaseSha))
                details["salvageAuthoritativeBaseSha"] = req.SalvageAuthoritativeBaseSha;
            if (!string.IsNullOrWhiteSpace(reportedReason))
                details["reason"] = reportedReason;
            RemoteClaimFailureDecision? claimFailure = null;
            if (outcome == "environmentfailure")
            {
                claimFailure = remoteClaimFailures.Record(task, reportedReason);
                details["attempt"] = claimFailure.Attempt.ToString();
                details["maximumAttempts"] = claimFailure.MaximumAttempts.ToString();
            }
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
            if (!string.IsNullOrWhiteSpace(salvageBranch)
                && !string.IsNullOrWhiteSpace(salvageCommitSha))
            {
                var resultsDir = TaskPaths.ResultsDir(task.FolderPath);
                Directory.CreateDirectory(resultsDir);
                var deliverablesPath = Path.Combine(resultsDir, "deliverables.md");
                var branchRef = !string.IsNullOrWhiteSpace(salvageBranchUrl)
                    ? $"[{salvageBranch}]({salvageBranchUrl})"
                    : $"`{salvageBranch}`";
                var recoveryLine = !string.IsNullOrWhiteSpace(req.SalvageRecoveryBranch)
                    && !string.IsNullOrWhiteSpace(req.SalvageRecoveryCommitSha)
                    ? $"- Divergent local history preserved on " +
                      (!string.IsNullOrWhiteSpace(salvageRecoveryBranchUrl)
                          ? $"[{req.SalvageRecoveryBranch}]({salvageRecoveryBranchUrl})"
                          : $"`{req.SalvageRecoveryBranch}`") +
                      $" at `{req.SalvageRecoveryCommitSha}`; " +
                      $"`{req.SalvageAuthoritativeBaseBranch ?? salvageBranch}` at " +
                      $"`{req.SalvageAuthoritativeBaseSha ?? salvageCommitSha}` was the authoritative pickup base.{Environment.NewLine}"
                    : string.Empty;
                File.WriteAllText(
                    deliverablesPath,
                    $"# Remote runner deliverables{Environment.NewLine}{Environment.NewLine}" +
                    $"- Salvage branch {branchRef} at `{salvageCommitSha}`.{Environment.NewLine}" +
                    recoveryLine,
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
                    runId: attemptId,
                    details: details))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Remote completion timeline persistence failed",
                    detail: $"RunAttempt '{attemptId}' is settled, but its idempotent timeline fact was not persisted. Retry the same completion delivery.");
            }

            var laneWrite = new AttemptWriteReference(
                attemptId,
                req.FencingToken,
                epoch,
                $"lane-completion:{completionKey}");

            if (claimFailure is not null)
            {
                var reason =
                    $"Remote claim repository/environment preparation failed " +
                    $"({claimFailure.Attempt}/{claimFailure.MaximumAttempts}): {claimFailure.Reason}";
                if (!claimFailure.Escalate)
                {
                    var retryMove = await transitions.MoveAsync(
                        task.Id,
                        TaskStates.Ready,
                        task.WatchPath,
                        ct,
                        cause: $"remote-claim-environment-retry:{claimFailure.Attempt}/{claimFailure.MaximumAttempts}",
                        authorityWrite: laneWrite,
                        suppressProductExecution: true);
                    if (retryMove.Status != MoveJobStatus.Success)
                        return Results.Conflict(new RemoteRunCompletionResponse(
                            req.TaskKey,
                            reportedOutcome,
                            task.State,
                            $"Environment retry lane move refused: {retryMove.Status} {retryMove.Message}",
                            RunAttemptId: attemptId));

                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                        "remote-claim-environment-retry project={Project} task={TaskKey} runner={Runner} attempt={Attempt}/{MaximumAttempts} reason={Reason}",
                        task.ProjectName,
                        req.TaskKey,
                        source,
                        claimFailure.Attempt,
                        claimFailure.MaximumAttempts,
                        claimFailure.Reason);
                    return Results.Ok(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        TaskStates.Ready,
                        reason,
                        RunAttemptId: attemptId));
                }

                var escalated = await humanReviewEscalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    HumanReviewEscalationCategories.RemoteClaimEnvironment,
                    reason,
                    ct,
                    laneWrite);
                if (escalated.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        task.State,
                        $"Environment escalation lane move refused: {escalated.Status} {escalated.Message}",
                        RunAttemptId: attemptId));

                timeline.Append(
                    escalated.NewFolderPath ?? task.FolderPath,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.System,
                    reason,
                    runId: attemptId,
                    details: new Dictionary<string, string>
                    {
                        ["category"] = HumanReviewEscalationCategories.RemoteClaimEnvironment,
                        ["attempt"] = claimFailure.Attempt.ToString(),
                        ["maximumAttempts"] = claimFailure.MaximumAttempts.ToString(),
                        ["reason"] = claimFailure.Reason,
                    });
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogError(
                    "remote-claim-environment-escalated project={Project} task={TaskKey} runner={Runner} attempt={Attempt}/{MaximumAttempts} reason={Reason}",
                    task.ProjectName,
                    req.TaskKey,
                    source,
                    claimFailure.Attempt,
                    claimFailure.MaximumAttempts,
                    claimFailure.Reason);
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey,
                    reportedOutcome,
                    TaskStates.Escalated,
                    reason,
                    RunAttemptId: attemptId));
            }

            remoteClaimFailures.Reset(task);

            if (isEpicPlanning)
            {
                // Epic planning is source-read-only and owns no ReviewSubject.
                // It still uses the canonical RunAttempt for its lane write and
                // completion evidence, then delegates child creation to the
                // shared idempotent decomposition lifecycle.
                if (!string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                {
                    var planningMove = states.MoveJob(
                        task.Id, TaskStates.AutoReview, task.WatchPath,
                        cause: $"remote-epic-planning-completion:{source}",
                        authorityWrite: laneWrite);
                    if (planningMove.Status != MoveJobStatus.Success)
                        return Results.Conflict(new RemoteRunCompletionResponse(
                            req.TaskKey, reportedOutcome, task.State,
                            $"Epic planning lane move refused: {planningMove.Status} {planningMove.Message}",
                            RunAttemptId: attemptId));
                }

                task = scanner.FindJob(task.Id, task.WatchPath) ?? task;
                var invalidationReason = req.SourceMutated
                    ? "Epic planning mutated the read-only product checkout"
                    : outcome is not ("done" or "noop")
                        ? $"Epic planning ended with non-success outcome '{outcome}'"
                        : null;
                var finalized = EpicDecompositionLifecycle.Finalize(
                    task, req.OutputLines, req.LeaseId, projectSettings, mutations, scanner,
                    states, timeline, chatLog,
                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteEpicPlanning"),
                    invalidationReason);
                if (!finalized.Valid)
                {
                    loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogWarning(
                        "remote-epic-planning-invalid task={TaskKey} runner={Runner} sourceMutated={SourceMutated} reason={Reason}",
                        req.TaskKey, source, req.SourceMutated, finalized.Error);
                    return Results.Ok(new RemoteRunCompletionResponse(
                        req.TaskKey, reportedOutcome, TaskStates.Backlog,
                        req.SourceMutated
                            ? "Epic planning attempted to mutate the read-only checkout; no children were created."
                            : finalized.Error,
                        RunAttemptId: attemptId));
                }
                loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerCompletion").LogInformation(
                    "remote-epic-planning-completion project={Project} task={TaskKey} runner={Runner} children={Children} token={FencingToken}",
                    task.ProjectName, req.TaskKey, source, finalized.CreatedTaskIds.Count, req.FencingToken);
                if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal epicPrincipal)
                {
                    accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                        DateTime.UtcNow, "completion", req.TaskKey, task.ProjectName,
                        InitiatingPrincipal(task.OwnerClientId), epicPrincipal.RunnerId, epicPrincipal.CredentialId,
                        req.FencingToken, outcome));
                }
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.AutoReview,
                    RunAttemptId: attemptId));
            }

            if (targetState == TaskStates.Escalated)
            {
                var (category, reason) = RemoteEscalation(outcome, req.Reason);
                var escalated = await humanReviewEscalation.EscalateAsync(
                    task.Id,
                    task.WatchPath,
                    task.ProjectName,
                    category,
                    reason,
                    ct,
                    laneWrite);
                if (escalated.Status != MoveJobStatus.Success)
                    return Results.Conflict(new RemoteRunCompletionResponse(
                        req.TaskKey,
                        reportedOutcome,
                        task.State,
                        $"Escalation lane move refused: {escalated.Status} {escalated.Message}",
                        RunAttemptId: attemptId));
                timeline.Append(
                    escalated.NewFolderPath ?? task.FolderPath,
                    TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.System,
                    reason,
                    runId: attemptId,
                    details: new Dictionary<string, string>
                    {
                        ["category"] = category,
                        ["reason"] = reason,
                    });
                return Results.Ok(new RemoteRunCompletionResponse(
                    req.TaskKey,
                    reportedOutcome,
                    TaskStates.Escalated,
                    reason,
                    RunAttemptId: attemptId));
            }

            if (!string.Equals(task.State, targetState, StringComparison.OrdinalIgnoreCase))
            {
                var move = await transitions.MoveAsync(
                    task.Id, targetState, task.WatchPath, ct,
                    cause: $"remote-runner-completion:{source}",
                    authorityWrite: laneWrite,
                    suppressProductExecution: true);
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
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal runnerPrincipal)
            {
                accessSecurity.AppendRunAudit(new RunSecurityAuditEvent(
                    DateTime.UtcNow, "completion", req.TaskKey, task.ProjectName,
                    InitiatingPrincipal(task.OwnerClientId), runnerPrincipal.RunnerId, runnerPrincipal.CredentialId,
                    req.FencingToken, outcome));
            }
            return Results.Ok(new RemoteRunCompletionResponse(
                req.TaskKey, reportedOutcome, targetState,
                RunAttemptId: attemptId,
                ReviewAttemptId: reviewAttempt?.AttemptId,
                ReviewSubjectId: reviewAttempt?.Subject.SubjectId));
            }
            finally
            {
                ClaimGate.Release();
            }
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

    private static bool SameRepositoryUrl(string? actual, string expected) =>
        string.Equals(actual?.Trim().TrimEnd('/'), expected.Trim().TrimEnd('/'), StringComparison.Ordinal);

    private static string OneLine(string? value)
    {
        var clean = (value ?? "preflight failed").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 1000 ? clean : clean[..1000];
    }

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

    private static bool RunnerMatches(HttpContext context, string runnerId, string? runnerName = null)
    {
        if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal) return true;
        return string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal)
               && (runnerName is null || string.Equals(principal.RunnerName, runnerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string InitiatingPrincipal(string? ownerClientId)
        => string.IsNullOrWhiteSpace(ownerClientId) ? "automation:unknown" : ownerClientId;

    private static (string Category, string Reason) RemoteEscalation(string outcome, string? reportedReason)
    {
        var reason = CredentialRedactor.Redact(reportedReason)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return outcome switch
        {
            "blocked" when reason.StartsWith(
                "Remote environment preparation failed after ",
                StringComparison.OrdinalIgnoreCase)
                => (
                    HumanReviewEscalationCategories.RemoteEnvironmentPreparation,
                    reason),
            "blocked" => (
                HumanReviewEscalationCategories.AgentBlocked,
                reason.Length == 0
                    ? "The remote agent reported that it could not continue."
                    : $"The remote agent reported a blocker: {reason}"),
            "needsinput" => (
                HumanReviewEscalationCategories.NeedsHumanInput,
                reason.Length == 0
                    ? "The remote agent requires operator input before it can continue."
                    : $"The remote agent requires operator input: {reason}"),
            _ => (
                HumanReviewEscalationCategories.RemoteOutcomeUnknown,
                reason.Length == 0
                    ? "The remote runner ended without a recognized terminal outcome."
                    : $"The remote runner ended without a recognized terminal outcome: {reason}"),
        };
    }
}
