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

        group.MapPost("/acquire", (RunLeaseAcquireRequest req, HttpContext context, ITaskScanner scanner, ProjectSettingsService settings, RunLeaseService leases, RunnerIdentity identity) =>
        {
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName)) return Results.Unauthorized();
            var task = FindTask(scanner, req.TaskKey);
            if (task is null)
                return Results.NotFound(new RunLeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal principal)
            {
                var project = settings.Get(task.ProjectName);
                var assigned = project.ExecutionRunner;
                if (!project.RemoteExecutionEnabled || string.IsNullOrWhiteSpace(assigned)
                    || !(string.Equals(assigned, principal.RunnerId, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(assigned, principal.RunnerName, StringComparison.OrdinalIgnoreCase)))
                    return Results.Json(new RunLeaseResponse(
                        "ProjectDenied", false, null,
                        "The Runner is not assigned to this remote-enabled project."), statusCode: StatusCodes.Status403Forbidden);
            }
            return Results.Ok(leases.TryAcquire(StampIdentity(req, identity)));
        });

        group.MapPost("/renew", (RunLeaseHeartbeatRequest req, HttpContext context, RunLeaseService leases) =>
            RunnerMatches(context, req.RunnerId) ? Results.Ok(leases.Renew(req)) : Results.Unauthorized());

        group.MapPost("/release", (RunLeaseReleaseRequest req, HttpContext context, RunLeaseService leases) =>
            RunnerMatches(context, req.RunnerId) ? Results.Ok(leases.Release(req)) : Results.Unauthorized());

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
            AccessSecurityStore accessSecurity,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Tasks.RemoteRunnerClaim");
            if (string.IsNullOrWhiteSpace(req.RunnerId) || string.IsNullOrWhiteSpace(req.RunnerName))
                return Results.BadRequest(new RunnerClaimResponse(RunnerClaimStatus.Invalid, Message: "runnerId and runnerName are required."));
            if (!RunnerMatches(context, req.RunnerId, req.RunnerName))
                return Results.Unauthorized();

            var clientId = context.Items["ClientId"] as string ?? context.Request.Headers["X-Client-Id"].ToString();
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
                    req.Pid, req.BackendName, req.RequestedTtlSeconds));
                if (!acquire.Granted || acquire.Lease is null)
                    return Results.Ok(new RunnerClaimResponse(RunnerClaimStatus.Empty, Message: acquire.Message ?? acquire.Outcome));

                var move = await transitions.MoveAsync(
                    candidate.Id, TaskStates.Progress, candidate.WatchPath, ct,
                    cause: $"remote-runner:{req.RunnerName.Trim()}");
                if (move.Status != MoveJobStatus.Success)
                {
                    leases.Release(new RunLeaseReleaseRequest(
                        taskKey, acquire.Lease.LeaseId, acquire.Lease.FencingToken, req.RunnerId.Trim()));
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
                if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is RunnerPrincipal runnerPrincipal)
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
                    DefaultBranch: repository.DefaultBranch));
            }
            finally
            {
                ClaimGate.Release();
            }
        });

        app.MapPost("/api/runner/completion", async (
            RemoteRunCompletionRequest req,
            HttpContext context,
            TaskScannerService scanner,
            TaskTransitionService transitions,
            RunLeaseService leases,
            TimelineLog timeline,
            AccessSecurityStore accessSecurity,
            WorkspaceArtifactCommitService artifactCommits,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!RunnerMatches(context, req.RunnerId)) return Results.Unauthorized();
            var reportedOutcome = req.Outcome ?? string.Empty;
            if (!leases.IsCurrent(req.TaskKey, req.LeaseId, req.FencingToken, req.RunnerId))
                return Results.Conflict(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, TaskStates.Progress,
                    "Lease id, fencing token, or runner id does not match the current holder."));

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

            var source = CredentialRedactor.Redact(string.IsNullOrWhiteSpace(req.Source) ? req.RunnerId : req.Source.Trim());
            var salvageBranch = CredentialRedactor.Redact(req.SalvageBranch);
            var salvageCommitSha = CredentialRedactor.Redact(req.SalvageCommitSha);
            var salvageBranchUrl = CredentialRedactor.Redact(req.SalvageBranchUrl);
            var details = new Dictionary<string, string>
            {
                ["cli"] = "remote-runner",
                ["status"] = outcome,
                ["runner"] = source,
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
            if (!string.IsNullOrWhiteSpace(salvageBranch)
                && !string.IsNullOrWhiteSpace(salvageCommitSha))
            {
                var resultsDir = TaskPaths.ResultsDir(task.FolderPath);
                Directory.CreateDirectory(resultsDir);
                var deliverablesPath = Path.Combine(resultsDir, "deliverables.md");
                var branchRef = !string.IsNullOrWhiteSpace(salvageBranchUrl)
                    ? $"[{salvageBranch}]({salvageBranchUrl})"
                    : $"`{salvageBranch}`";
                File.WriteAllText(
                    deliverablesPath,
                    $"# Remote runner deliverables{Environment.NewLine}{Environment.NewLine}" +
                    $"- Salvage branch {branchRef} at `{salvageCommitSha}`.{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
                artifactCommits.TryCommitArtifactUpload(
                    null, task.Id, task.FolderPath, ["results/deliverables.md"]);
            }
            timeline.Append(
                task.FolderPath,
                TimelineEventKinds.AgentRunFinished,
                TimelineActors.Agent,
                summary: $"remote run {outcome} on {source}",
                details: details);

            var move = await transitions.MoveAsync(
                task.Id, targetState, task.WatchPath, ct,
                cause: $"remote-runner-completion:{source}");
            if (move.Status != MoveJobStatus.Success)
                return Results.Conflict(new RemoteRunCompletionResponse(
                    req.TaskKey, reportedOutcome, task.State, $"Lane move refused: {move.Status} {move.Message}"));

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
            return Results.Ok(new RemoteRunCompletionResponse(req.TaskKey, reportedOutcome, targetState));
        });
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
}
