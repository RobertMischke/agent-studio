using System.Net.Http;

namespace AgentRunner;

/// <summary>
/// Continuously fills a bounded set of host slots from the Task Server's
/// assignment-aware claim endpoint. Each claim already owns a fenced lease and
/// is executed in its own linked git worktree by <see cref="RemoteTaskRunner"/>.
///
/// <para>
/// The daemon is long-lived and the Task Server is reached over a link that is
/// expected to blip (the backend restarts on deploy; a reverse tunnel drops). A
/// blip must never churn the daemon: detached workers can survive a planned
/// handoff, but repeated main-process exits would interrupt heartbeats and delay
/// output delivery. Transient connectivity faults are therefore absorbed here
/// (retry with backoff) instead of bubbling up to the fatal exit-4 handler.
/// </para>
/// </summary>
public sealed class RemoteRunnerDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteRunnerDaemon(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    /// <summary>
    /// A fault that means "the Task Server is momentarily unreachable or unwell",
    /// not "this runner is misconfigured": transport failures, HttpClient timeouts,
    /// and server-side (5xx) replies. These are retried; anything else - a 4xx that
    /// signals a real client/protocol problem, or an unexpected exception - is left
    /// to propagate so it is not silently masked.
    /// </summary>
    internal static bool IsTransientServerFault(Exception ex) => ex switch
    {
        HttpRequestException => true,
        TaskCanceledException => true, // HttpClient request timeout (shutdown is checked separately by callers)
        TaskServerException tse => tse.StatusCode >= 500,
        _ => false,
    };

    /// <summary>
    /// Run a Task Server call, absorbing transient connectivity faults with a
    /// bounded backoff until it succeeds or shutdown is requested. Used for the
    /// one-time startup calls so a server that is briefly down at boot no longer
    /// costs a fatal exit and a systemd restart cycle.
    /// </summary>
    private async Task<T> WithServerRetryAsync<T>(
        string what,
        Func<Task<T>> call,
        TaskServerConnectivityMonitor connectivity,
        Func<int> activeSlots,
        CancellationToken shutdown)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                var result = await call();
                connectivity.RecordSuccess(DateTime.UtcNow, what);
                return result;
            }
            catch (Exception ex) when (IsTransientServerFault(ex) && !shutdown.IsCancellationRequested)
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(_options.PollSeconds, attempt);
                connectivity.RecordFailure(DateTime.UtcNow, what, ex, delay, activeSlots());
                await Task.Delay(delay, shutdown);
            }
        }
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        var connectivity = new TaskServerConnectivityMonitor(_log);
        var state = new RunnerStateStore(_options.StateDir);
        var persistedAtStartup = state.LoadAll();
        using var startupAuthorityWatch = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown);
        var startupAuthorityTasks = persistedAtStartup
            .Select(slot => EnforcePersistedAuthorityDeadlineAsync(
                slot,
                state,
                startupAuthorityWatch.Token))
            .ToArray();

        var clientId = await WithServerRetryAsync(
            "runner registration",
            () => _client.RegisterAsync(_options.RunnerName, "service", shutdown),
            connectivity,
            () => 0,
            shutdown);
        _log(
            $"authenticated daemon '{_options.RunnerName}' with attribution '{clientId}'; " +
            $"slots={_client.HostMaxParallelism}");
        var handoffRecovery = new DurableHandoffRecovery(_options, _client, _log);

        var inventory = new RunnerProcessInventoryTracker();
        var active = new List<ActiveSlot>();
        foreach (var persisted in state.LoadAll())
        {
            var slot = await RecoverLaunchingIdentityAsync(persisted, state);
            _client.RestoreRunAuthority(slot.TaskKey, slot.RunId, slot.LeaseInstanceId, slot.Lease);
            var taskRunner = new RemoteTaskRunner(
                _options,
                _client,
                _log,
                state,
                inventory);
            var completed = DurableAgentProcess.HasCompleted(slot);
            var live = DurableAgentProcess.VerifyLive(slot, out var verification);
            if (completed || live)
            {
                _log($"persisted attempt accepted task={slot.TaskKey} attempt={slot.AttemptId} " +
                     $"pid={slot.ProcessId} verification={(completed ? "durable result ready" : verification)}");
                active.Add(new ActiveSlot(
                    slot.TaskKey,
                    taskRunner.ReattachAsync(slot, CancellationToken.None)));
            }
            else
            {
                if (!await taskRunner.ReleaseDeadAsync(slot, verification))
                    throw new InvalidOperationException(
                        $"Dead attempt '{slot.AttemptId}' for task '{slot.TaskKey}' could not be released. " +
                        "Startup is fail-closed and retained the durable state for the next bounded systemd retry.");
            }
        }
        startupAuthorityWatch.Cancel();
        foreach (var watcher in startupAuthorityTasks)
        {
            try { await watcher; }
            catch (OperationCanceledException) { }
        }
        if (active.Count > 0)
            _log($"recovered {active.Count} persisted slot(s); no replacement claim will use those slots");
        await handoffRecovery.RecoverAllAsync(shutdown);

        // Recover heartbeats before the potentially slow fallback-remote probe.
        // This startup result is host diagnostics only. Delivery admission is
        // decided by the repository-specific preflight before each project can
        // receive a lease.
        var gitCapability = await GitPushProbe.RunAsync(_options, _log, shutdown);
        await WithServerRetryAsync<object?>(
            "git-capability report",
            async () =>
            {
                await _client.ReportGitCapabilityAsync(clientId, new RunnerGitCapabilityRequest(
                    gitCapability.Status, gitCapability.Detail, DateTime.UtcNow), shutdown);
                return null;
            },
            connectivity,
            () => active.Count,
            shutdown);
        _log($"runner-git-capability status={gitCapability.Status} detail={gitCapability.Detail}");
        if (!gitCapability.CanPush)
            _log("Configured fallback Git remote is read-only; project claims remain eligible and are gated by their own delivery preflight.");
        var capabilityGeneration = DateTime.UtcNow.Ticks;
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? latestTelemetry = telemetry.SampleIfDue(
            active.Count,
            connectivity.Snapshot);
        await WithServerRetryAsync<object?>(
            "capability advertisement",
            async () =>
            {
                await _client.AdvertiseCapabilitiesAsync(
                    RunnerCapabilityProbe.Advertise(
                        _options,
                        gitCapability.CanPush,
                        gitCapability.CanPushWorkflows,
                        gitCapability.Detail,
                        connectivity: connectivity.Snapshot),
                    RunnerCapabilityProbe.Telemetry(latestTelemetry),
                    capabilityGeneration,
                    shutdown);
                return null;
            },
            connectivity,
            () => active.Count,
            shutdown);
        if (!gitCapability.CanPush)
        {
            // Diagnosis only: read-only admission is already decided above, so a
            // server that rejects or does not mount the capability route must not
            // stop the daemon from coming up and recovering its slots.
            await CapabilityFailureReporter.TryReportAsync(
                _client,
                _log,
                AgentStudio.TaskServer.Contracts.CapabilityProtocol.GitPush,
                "GitPushUnavailable",
                gitCapability.Detail ?? "Git push probe failed.",
                $"startup-git-push:{_options.RunnerId}:{capabilityGeneration}",
                null,
                null,
                null,
                shutdown);
        }

        var loadGate = new RunnerLoadGate(
            _options.ClaimMaxLoadPerCore,
            TimeSpan.FromSeconds(_options.LoadGateSustainedSeconds));
        var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
        HostTelemetrySample? TakeTelemetry(bool force = false)
        {
            try
            {
                latestTelemetry = telemetry.SampleIfDue(
                    active.Count,
                    connectivity.Snapshot,
                    force) ?? latestTelemetry;
                return latestTelemetry;
            }
            catch (Exception ex)
            {
                _log($"host-telemetry-sample-failed error={ex.GetType().Name} message={ex.Message}");
                return null;
            }
        }
        var consecutiveFaults = 0;
        while (!shutdown.IsCancellationRequested)
        {
            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (!active[i].Execution.IsCompleted) continue;
                try { _log($"slot completed with exit code {await active[i].Execution}"); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception ex) { _log($"slot failed: {ex}"); }
                active.RemoveAt(i);
            }

            try
            {
                await handoffRecovery.RecoverAllAsync(shutdown);
                if (DateTime.UtcNow >= nextCapabilityAdvertisement)
                {
                    var capabilityTelemetry = TakeTelemetry();
                    await _client.AdvertiseCapabilitiesAsync(
                        RunnerCapabilityProbe.Advertise(
                            _options,
                            gitCapability.CanPush,
                            gitCapability.CanPushWorkflows,
                            gitCapability.Detail,
                            connectivity: connectivity.Snapshot),
                        RunnerCapabilityProbe.Telemetry(capabilityTelemetry),
                        ++capabilityGeneration,
                        shutdown);
                    nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
                }
                var claimedAny = false;
                var inventorySnapshot = inventory.Snapshot();
                var activeTaskKeys = inventorySnapshot.Processes
                    .Select(process => process.TaskKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var loadDecision = loadGate.Observe(
                    TakeTelemetry(),
                    DateTime.UtcNow);
                if (loadDecision.EmitEvent)
                {
                    inventory.Report(new RunnerInvariantReport(
                        $"inv_{Guid.NewGuid():N}",
                        "load-invariant",
                        DateTime.UtcNow,
                        "claim-stopped",
                        $"Claim admission stopped after load/core {loadDecision.LoadPerCore:0.00} remained high for {loadDecision.SustainedFor.TotalSeconds:0}s."));
                }
                if (loadDecision.Throttle)
                {
                    _log(
                        $"claim-load-gate closed loadPerCore={loadDecision.LoadPerCore:0.00} " +
                        $"threshold={_options.ClaimMaxLoadPerCore:0.00} " +
                        $"sustainedSeconds={loadDecision.SustainedFor.TotalSeconds:0} activeSlots={active.Count}");
                    inventorySnapshot = inventory.Snapshot();
                    activeTaskKeys = inventorySnapshot.Processes
                        .Select(process => process.TaskKey)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var response = await _client.ClaimAsync(new RunnerClaimRequest(
                        _options.RunnerId, _options.RunnerName, _options.Hostname,
                        Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
                        latestTelemetry,
                        AvailableSlots: 0,
                        ActiveSlots: active.Count,
                        IdempotencyKey: $"load-gate:{_options.RunnerId}:{Guid.NewGuid():N}",
                        ActiveTaskKeys: activeTaskKeys,
                        Inventory: inventorySnapshot), shutdown);
                    AcknowledgeInventory(inventory, inventorySnapshot, response);
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                    consecutiveFaults = 0;
                    if (connectivity.RecordSuccess(DateTime.UtcNow, "claim poll"))
                    {
                        TakeTelemetry(force: true);
                        nextCapabilityAdvertisement = DateTime.MinValue;
                    }
                    continue;
                }
                if (active.Count >= _client.HostMaxParallelism)
                {
                    inventorySnapshot = inventory.Snapshot();
                    activeTaskKeys = inventorySnapshot.Processes
                        .Select(process => process.TaskKey)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var response = await _client.ClaimAsync(new RunnerClaimRequest(
                            _options.RunnerId, _options.RunnerName, _options.Hostname,
                            Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
                            TakeTelemetry(), AvailableSlots: 0,
                            ActiveSlots: active.Count,
                            IdempotencyKey: $"telemetry:{_options.RunnerId}:{Guid.NewGuid():N}",
                            ActiveTaskKeys: activeTaskKeys,
                            Inventory: inventorySnapshot), shutdown);
                    AcknowledgeInventory(inventory, inventorySnapshot, response);
                }
                while (active.Count < _client.HostMaxParallelism && !shutdown.IsCancellationRequested)
                {
                    var chatClaim = await _client.ClaimProjectChatWorkAsync(
                        new RemoteChatWorkClaimRequest(
                            _options.RunnerId, _options.RunnerName, _options.Hostname),
                        shutdown);
                    if (chatClaim.Status == RemoteChatWorkClaimStatuses.Claimed
                        && chatClaim.Work is not null)
                    {
                        claimedAny = true;
                        _log(
                            $"claimed project chat {chatClaim.Work.ProjectName}/{chatClaim.Work.Kind} " +
                            $"into slot {active.Count + 1}/{_client.HostMaxParallelism}");
                        active.Add(new ActiveSlot(
                            null,
                            new RemoteProjectChatRunner(_options, _client, _log)
                                .RunAsync(chatClaim.Work, shutdown)));
                        continue;
                    }

                    inventorySnapshot = inventory.Snapshot();
                    activeTaskKeys = inventorySnapshot.Processes
                        .Select(process => process.TaskKey)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var claim = await ClaimWithProjectPreflightAsync(new RunnerClaimRequest(
                        _options.RunnerId, _options.RunnerName, _options.Hostname,
                        Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
                        TakeTelemetry(),
                        AvailableSlots: _client.HostMaxParallelism - active.Count,
                        ActiveSlots: active.Count,
                        IdempotencyKey: $"claim:{_options.RunnerId}:{Guid.NewGuid():N}",
                        ActiveTaskKeys: activeTaskKeys,
                        Inventory: inventorySnapshot),
                        // A claim is an atomic server-side mutation. Once sent,
                        // do not cancel the HTTP request on SIGTERM.
                        CancellationToken.None,
                        shutdown);
                    AcknowledgeInventory(inventory, inventorySnapshot, claim);
                    if (claim.Status != RunnerClaimStatus.Claimed
                        || string.IsNullOrWhiteSpace(claim.TaskKey)
                        || claim.Lease is null)
                    {
                        if (claim.Status is RunnerClaimStatus.PreflightFailed or RunnerClaimStatus.Invalid)
                            _log($"claim refused status={claim.Status} project={claim.ProjectName ?? "unknown"} reason={claim.Message ?? "no detail"}");
                        break;
                    }

                    var taskRunner = new RemoteTaskRunner(
                        _options,
                        _client,
                        _log,
                        state,
                        inventory);
                    if (shutdown.IsCancellationRequested)
                    {
                        var workspace = new GitWorkspace(
                            _options, claim.TaskKey, _log,
                            claim.ProjectId, claim.RepositoryUrl, claim.DefaultBranch);
                        var slot = state.Create(
                            claim.TaskKey, claim.Lease, workspace.RepoPath,
                            claim.RunId, claim.LeaseInstanceId, claim.ProjectId,
                            claim.RepositoryUrl, claim.DefaultBranch, claim.TaskKind,
                            claim.RunSpec);
                        const string reason = "planned daemon shutdown completed an in-flight claim before worker start";
                        _log($"releasing claim completed during shutdown task={claim.TaskKey} lease={claim.Lease.LeaseId}");
                        if (!await taskRunner.ReleaseDeadAsync(slot, reason))
                            throw new InvalidOperationException(
                                $"Claim '{claim.Lease.LeaseId}' completed during shutdown but could not be released. " +
                                "Durable state was retained for replacement startup.");
                        break;
                    }

                    claimedAny = true;
                    _log($"claimed {claim.ProjectName}/{claim.TaskKey} using project cache {claim.ProjectId ?? "legacy fallback"} into slot {active.Count + 1}/{_client.HostMaxParallelism}");
                    active.Add(new ActiveSlot(
                        claim.TaskKey,
                        taskRunner.RunClaimedAsync(
                            claim.TaskKey,
                            claim.Lease,
                            CancellationToken.None,
                            claim.ProjectId,
                            claim.RepositoryUrl,
                            claim.DefaultBranch,
                            claim.TaskKind,
                            claim.RunId,
                            claim.LeaseInstanceId,
                            // T0b: the card's execution spec. Null from a server
                            // that predates it - the runner then falls back to
                            // its RUNNER_CLI_* configuration as before.
                            claim.RunSpec)));
                }

                if (!claimedAny)
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                consecutiveFaults = 0;
                if (connectivity.RecordSuccess(DateTime.UtcNow, "claim poll"))
                {
                    TakeTelemetry(force: true);
                    nextCapabilityAdvertisement = DateTime.MinValue;
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Clean shutdown: the loop ends and leaves in-flight detached
                // workers for the replacement daemon.
            }
            catch (Exception ex) when (IsTransientServerFault(ex))
            {
                // The Task Server blipped while we polled for work. This is the exact
                // fault that used to bubble to Program.cs and exit the process with
                // code 4 - killing the whole cgroup and stranding leases. Instead:
                // keep every in-flight slot running (their heartbeats tolerate the
                // same blip) and retry the claim after a bounded backoff.
                var delay = TaskServerConnectivityMonitor.RetryDelay(
                    _options.PollSeconds,
                    ++consecutiveFaults);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    "claim poll",
                    ex,
                    delay,
                    active.Count);
                TakeTelemetry(force: true);
                await DelayThroughShutdown(delay, shutdown);
            }
        }

        // Planned restart is a handoff, not job cancellation. Slot state was
        // atomically flushed at claim/process/output boundaries and detached
        // workers are intentionally left alive for the replacement daemon.
        state.Flush();
        _log($"daemon drain complete; leaving {active.Count} detached job(s) for startup reattach");
    }

    private async Task EnforcePersistedAuthorityDeadlineAsync(
        PersistedRunnerSlot slot,
        RunnerStateStore state,
        CancellationToken adopted)
    {
        var durable = DurableLeaseAuthority.Read(slot.WorkerDirectory);
        var stopBefore = durable?.StopBeforeUtc
                         ?? DurableLeaseAuthority.ComputeStopBefore(
                             slot.Lease.ExpiresAt,
                             TimeSpan.FromSeconds(
                                 Math.Max(5, _options.HeartbeatSeconds)));
        var remaining = stopBefore - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, adopted);
        adopted.ThrowIfCancellationRequested();

        _log(
            $"persisted authority deadline exhausted task={slot.TaskKey} " +
            $"attempt={slot.AttemptId} stop-before={stopBefore:o}; " +
            "reaping the contained process generation before any replacement");
        await WorktreeProcessReaper.ReapAsync(
            slot.WorktreePath,
            _log,
            CancellationToken.None);
        state.Save(slot with
        {
            Phase = "authority-deadline-exhausted",
        });
        _log(
            $"persisted authority generation death proven task={slot.TaskKey} " +
            $"attempt={slot.AttemptId}; durable state retained for honest release reconciliation");
    }

    private async Task<PersistedRunnerSlot> RecoverLaunchingIdentityAsync(
        PersistedRunnerSlot slot,
        RunnerStateStore state)
    {
        if (slot.ProcessId is not null || DurableAgentProcess.HasCompleted(slot))
            return slot;

        // "launching" is persisted before Process.Start. The worker writes its
        // own atomic identity before it starts the CLI, so a replacement waits
        // briefly for that proof instead of releasing a child which was merely
        // between Process.Start and the daemon's PID slot write.
        var attempts = string.Equals(slot.Phase, "launching", StringComparison.Ordinal)
            ? 20
            : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (DurableAgentProcess.TryRecoverIdentity(slot, out var recovered, out var reason))
            {
                _log($"recovered worker identity task={slot.TaskKey} pid={recovered.ProcessId}: {reason}");
                return state.Save(recovered with { Phase = "running" });
            }

            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return slot;
    }

    private async Task<RunnerClaimResponse> ClaimWithProjectPreflightAsync(
        RunnerClaimRequest request,
        CancellationToken claimCancellation,
        CancellationToken preflightCancellation)
    {
        var claim = await _client.ClaimAsync(request, claimCancellation);
        if (claim.Status != RunnerClaimStatus.PreflightRequired) return claim;
        if (string.IsNullOrWhiteSpace(claim.ProjectId)
            || string.IsNullOrWhiteSpace(claim.RepositoryUrl)
            || string.IsNullOrWhiteSpace(claim.DefaultBranch)
            || string.IsNullOrWhiteSpace(claim.RegistrationFingerprint))
            return claim with
            {
                Status = RunnerClaimStatus.Invalid,
                Message = "Server requested project preflight without project id, repository URL, and registration fingerprint."
            };

        _log($"project-delivery-preflight-started project={claim.ProjectName ?? claim.ProjectId} projectId={claim.ProjectId}");
        var result = await GitWorkspace.PreflightProjectAsync(
            _options, claim.ProjectId, claim.RepositoryUrl, claim.DefaultBranch, _log, preflightCancellation);
        _log($"project-delivery-preflight-finished project={claim.ProjectName ?? claim.ProjectId} status={(result.Succeeded ? "ready" : "failed")} detail={result.Detail}");

        return await _client.ClaimAsync(request with
        {
            Telemetry = null,
            ProjectPreflight = new RunnerProjectPreflightReport(
                claim.ProjectId,
                claim.RegistrationFingerprint,
                result.Succeeded,
                result.Detail,
                DateTime.UtcNow,
                result.FetchUrl,
                result.PushUrl),
        }, claimCancellation);
    }

    private static async Task DelayThroughShutdown(TimeSpan delay, CancellationToken shutdown)
    {
        try { await Task.Delay(delay, shutdown); }
        catch (OperationCanceledException) { /* shutting down; the loop condition ends it */ }
    }

    private void AcknowledgeInventory(
        RunnerProcessInventoryTracker inventory,
        RunnerProcessInventory snapshot,
        RunnerClaimResponse response)
    {
        if (_client.UsesDurableTaskServer)
            inventory.AcknowledgeReports(snapshot);
        inventory.Apply(response.ReconciliationActions);
    }

    private sealed record ActiveSlot(string? TaskKey, Task<int> Execution);
}
