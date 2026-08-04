using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Bounded daemon loop for the separately registered review service. Persisted
/// attempts are continued before load-aware admission may claim new work.
/// </summary>
public sealed class RemoteReviewDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteReviewDaemon(RunnerOptions options, TaskServerClient client, Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        await using var idleWatchdog = new DaemonIdleWatchdog(
            _log,
            TimeSpan.FromMinutes(_options.IdleWatchdogMinutes));
        using var daemonStop = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown,
            idleWatchdog.AbortToken);
        shutdown = daemonStop.Token;
        var state = new ReviewStateStore(_options.StateDir);
        var persistedAtStartup = state.LoadAll();
        var active = new List<(Task<int> Run, string AttemptId, string ResourceNamespace)>();
        var connectivity = new TaskServerConnectivityMonitor(_log);
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? latestTelemetry = null;

        HostTelemetrySample? TakeTelemetry(bool force = false)
        {
            try
            {
                latestTelemetry = force
                    ? telemetry.SampleNow(active.Count, connectivity.Snapshot)
                    : telemetry.SampleIfDue(active.Count, connectivity.Snapshot) ?? latestTelemetry;
                return latestTelemetry;
            }
            catch (Exception exception)
            {
                _log(
                    $"review host telemetry sample failed error={exception.GetType().Name} " +
                    $"message={exception.Message}");
                return null;
            }
        }

        try
        {
            await WithServerRetryAsync(
                "review registration",
                () => _client.RegisterAsync(_options.RunnerName, "review-executor", shutdown),
                connectivity,
                () => Math.Max(active.Count, persistedAtStartup.Count),
                shutdown);

        foreach (var persisted in persistedAtStartup)
        {
            var slot = await RecoverLaunchingIdentityAsync(persisted, state, shutdown);
            var completed = DurableReviewProcess.HasCompleted(slot);
            var live = DurableReviewProcess.VerifyLive(slot, out var verification);
            var executor = new RemoteReviewExecutor(_options, _client, state, _log);
            if (completed || live)
            {
                _log(
                    $"persisted review accepted attempt={slot.AttemptId} " +
                    $"fence={slot.Claim.Lease!.Fence} " +
                    $"verification={(completed ? "durable result ready" : verification)}");
                active.Add((
                    executor.ReattachAsync(slot, shutdown),
                    slot.AttemptId,
                    slot.Claim.Lease!.ResourceNamespace));
            }
            else
            {
                active.Add((
                    executor.ReportNonAdoptableAsync(slot, verification, shutdown),
                    slot.AttemptId,
                    slot.Claim.Lease!.ResourceNamespace));
            }
        }
        if (active.Count > 0)
        {
            _log(
                $"recovering {active.Count} persisted review slot(s) before replacement claims; " +
                "load admission applies only to fresh slots");
        }
        idleWatchdog.RecordActiveSlots(active.Count);

        var capabilityGeneration = DateTime.UtcNow.Ticks;
        await CapabilityAdvertisementRecovery.ExecuteAsync(
            "review capability advertisement",
            async ct =>
            {
                await _client.AdvertiseCapabilitiesAsync(
                    RunnerCapabilityProbe.Advertise(
                        _options,
                        gitPushReady: false,
                        connectivity: connectivity.Snapshot),
                    RunnerCapabilityProbe.Telemetry(TakeTelemetry(force: true)),
                    capabilityGeneration,
                    ct);
            },
            async ct =>
            {
                _ = await _client.RegisterAsync(_options.RunnerName, "review-executor", ct);
            },
            connectivity,
            () => active.Count,
            _options.PollSeconds,
            TimeSpan.FromSeconds(_options.ServerRequestTimeoutSeconds),
            _log,
            shutdown);

        var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
        var admissionClosed = false;
        var nextRetentionSweep = DateTime.MinValue;
        var consecutiveFaults = 0;
        while (!shutdown.IsCancellationRequested)
        {
            idleWatchdog.RecordActiveSlots(active.Count);
            for (var index = active.Count - 1; index >= 0; index--)
            {
                if (!active[index].Run.IsCompleted) continue;
                try
                {
                    var exitCode = await active[index].Run;
                    _log($"remote review slot finished exit={exitCode}");
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    _log("remote review slot stopped during shutdown");
                }
                catch (Exception exception)
                {
                    _log($"remote review slot failed after cleanup: {exception.Message}");
                }
                active.RemoveAt(index);
            }
            idleWatchdog.RecordActiveSlots(active.Count);

            try
            {
                if (DateTime.UtcNow >= nextRetentionSweep)
                {
                    try
                    {
                        ReviewWorkspaceRetention.Sweep(
                            _options.ReviewWorkDir,
                            active.Select(slot => slot.ResourceNamespace),
                            DateTime.UtcNow,
                            _log);
                    }
                    catch (Exception exception)
                    {
                        _log(
                            "review workspace retention sweep failed; " +
                            $"retrying next interval: {exception.Message}");
                    }
                    nextRetentionSweep = DateTime.UtcNow.AddHours(1);
                }
                var observedServer = false;
                var admissionTelemetry = TakeTelemetry(force: true);
                if (DateTime.UtcNow >= nextCapabilityAdvertisement)
                {
                    var generation = ++capabilityGeneration;
                    await CapabilityAdvertisementRecovery.ExecuteAsync(
                        "review capability advertisement",
                        ct => _client.AdvertiseCapabilitiesAsync(
                            RunnerCapabilityProbe.Advertise(
                                _options,
                                gitPushReady: false,
                                connectivity: connectivity.Snapshot),
                            RunnerCapabilityProbe.Telemetry(admissionTelemetry),
                            generation,
                            ct),
                        async ct =>
                        {
                            _ = await _client.RegisterAsync(
                                _options.RunnerName,
                                "review-executor",
                                ct);
                        },
                        connectivity,
                        () => active.Count,
                        _options.PollSeconds,
                        TimeSpan.FromSeconds(_options.ServerRequestTimeoutSeconds),
                        _log,
                        shutdown);
                    observedServer = true;
                    nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
                }

                idleWatchdog.RecordPollStarted();

                var admission = ReviewSlotAdmissionPolicy.Decide(
                    admissionTelemetry,
                    active.Count,
                    _options.HostMaxParallelism,
                    _options.ClaimMaxLoadPerCore);
                if (!admission.Admitted)
                {
                    if (!admissionClosed)
                    {
                        _log(
                            $"review slot admission closed: {admission.Reason}; " +
                            $"activeSlots={active.Count}");
                    }
                    admissionClosed = true;
                }
                else
                {
                    if (admissionClosed)
                    {
                        _log(
                            $"review slot admission reopened: {admission.Reason}; " +
                            $"activeSlots={active.Count}");
                    }
                    admissionClosed = false;

                    // Admission owns at most one new lease per fresh telemetry
                    // observation. Persisted continuations above do not pass
                    // through this gate and never lose completed test time.
                    var claim = await _client.ClaimReviewAsync(
                        new ReviewClaimRequest(
                            _options.RunnerId,
                            _client.RunnerInstanceId,
                            _options.TtlSeconds,
                            AvailableSlots: 1),
                        // A claim is an atomic authority mutation. Once sent,
                        // shutdown must not hide a successfully minted fence.
                        CancellationToken.None);
                    observedServer = true;
                    if (string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (active.Any(slot => string.Equals(
                                slot.AttemptId,
                                claim.Attempt!.AttemptId,
                                StringComparison.Ordinal)))
                        {
                            _log(
                                $"claim returned attempt {claim.Attempt!.AttemptId} already in flight " +
                                "on this host; skipping duplicate execution");
                        }
                        else
                        {
                            _log(
                                $"claimed remote review attempt={claim.Attempt!.AttemptId} " +
                                $"subject={claim.Subject!.SubjectId} " +
                                $"slot={active.Count + 1}/{_options.HostMaxParallelism}");
                            var executor = new RemoteReviewExecutor(_options, _client, state, _log);
                            var stale = state.Find(claim.Attempt.AttemptId);
                            if (stale is not null
                                && !DurableReviewProcess.HasCompleted(stale)
                                && !DurableReviewProcess.VerifyLive(stale, out var adoptionReason))
                            {
                                // A previous lease can expire before its loss
                                // report is accepted. Rebind only that terminal
                                // report to the new fence. An unproven process is
                                // never allowed to recover write authority.
                                stale = state.Save(stale with
                                {
                                    Claim = claim,
                                    Phase = "adoption-failed-reclaimed",
                                    AdoptionFailure = adoptionReason,
                                });
                                active.Add((
                                    executor.ReportNonAdoptableAsync(
                                        stale,
                                        adoptionReason,
                                        shutdown),
                                    claim.Attempt.AttemptId,
                                    claim.Lease!.ResourceNamespace));
                                idleWatchdog.RecordActiveSlots(active.Count);
                            }
                            else
                            {
                                active.Add((
                                    executor.RunClaimedAsync(claim, shutdown),
                                    claim.Attempt.AttemptId,
                                    claim.Lease!.ResourceNamespace));
                                idleWatchdog.RecordActiveSlots(active.Count);
                            }
                        }
                    }
                }

                consecutiveFaults = 0;
                if (observedServer
                    && connectivity.RecordSuccess(DateTime.UtcNow, "review claim poll"))
                {
                    TakeTelemetry(force: true);
                    nextCapabilityAdvertisement = DateTime.MinValue;
                }
                await DelayThroughShutdown(
                    TimeSpan.FromSeconds(_options.PollSeconds),
                    shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Active durable workers observe the handoff below.
            }
            catch (TaskServerException fatal) when (fatal.StatusCode is 401 or 403)
            {
                _log(
                    $"review claim poll rejected with {fatal.StatusCode}; " +
                    $"exiting for re-registration: {fatal.Message}");
                throw;
            }
            catch (Exception exception) when (RemoteRunnerDaemon.IsTransientServerFault(exception))
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(
                    _options.PollSeconds,
                    ++consecutiveFaults);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    "review claim poll",
                    exception,
                    delay,
                    active.Count);
                TakeTelemetry(force: true);
                await DelayThroughShutdown(delay, shutdown);
            }
            catch (Exception exception)
            {
                // A server-side conflict is visible but must not churn the
                // daemon or its detached workers.
                _log($"review claim poll failed; retrying next tick: {exception.Message}");
                await DelayThroughShutdown(
                    TimeSpan.FromSeconds(_options.PollSeconds),
                    shutdown);
            }
        }

        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            _log("review daemon startup stopped during Task Server communication");
        }

        state.Flush();
        if (active.Count > 0)
            await Task.WhenAll(active.Select(slot => slot.Run));
        _log(
            "review daemon drain complete; durable review workers are ready " +
            "for replacement adoption");
        if (idleWatchdog.Tripped)
            throw new InvalidOperationException(
                "The slot-free review daemon stopped polling and was terminated by its idle watchdog.");
    }

    private async Task<T> WithServerRetryAsync<T>(
        string operation,
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
                connectivity.RecordSuccess(DateTime.UtcNow, operation);
                return result;
            }
            catch (Exception exception) when (
                RemoteRunnerDaemon.IsTransientServerFault(exception)
                && !shutdown.IsCancellationRequested)
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(
                    _options.PollSeconds,
                    attempt);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    operation,
                    exception,
                    delay,
                    activeSlots());
                await Task.Delay(delay, shutdown);
            }
        }
    }

    private static async Task<PersistedReviewSlot> RecoverLaunchingIdentityAsync(
        PersistedReviewSlot slot,
        ReviewStateStore state,
        CancellationToken shutdown)
    {
        if (slot.ProcessId is not null || DurableReviewProcess.HasCompleted(slot))
            return slot;
        var attempts = string.Equals(slot.Phase, "launching", StringComparison.Ordinal)
            ? 20
            : 1;
        var reason = "no persisted review process identity";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (DurableReviewProcess.TryRecoverIdentity(slot, out var recovered, out reason))
                return state.Save(recovered with { Phase = "running" });
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown);
        }
        return state.Save(slot with { AdoptionFailure = reason });
    }

    private static async Task DelayThroughShutdown(
        TimeSpan delay,
        CancellationToken shutdown)
    {
        try { await Task.Delay(delay, shutdown); }
        catch (OperationCanceledException) { }
    }
}
