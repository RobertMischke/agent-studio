using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Bounded daemon loop for the separately registered review service.</summary>
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
        var state = new ReviewStateStore(_options.StateDir);
        await _client.RegisterAsync(_options.RunnerName, "review-executor", shutdown);
        var active = new List<(Task<int> Run, string AttemptId)>();
        foreach (var persisted in state.LoadAll())
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
                active.Add((executor.ReattachAsync(slot, shutdown), slot.AttemptId));
            }
            else
            {
                active.Add((
                    executor.ReportNonAdoptableAsync(slot, verification, shutdown),
                    slot.AttemptId));
            }
        }
        if (active.Count > 0)
            _log($"recovering {active.Count} persisted review slot(s) before replacement claims");
        var telemetry = new HostTelemetrySampler();
        var capabilityGeneration = DateTime.UtcNow.Ticks;
        await _client.AdvertiseCapabilitiesAsync(
            RunnerCapabilityProbe.Advertise(_options, gitPushReady: false),
            RunnerCapabilityProbe.Telemetry(telemetry.SampleIfDue(0)),
            capabilityGeneration,
            shutdown);
        var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
        while (!shutdown.IsCancellationRequested)
        {
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
            if (DateTime.UtcNow >= nextCapabilityAdvertisement)
            {
                await _client.AdvertiseCapabilitiesAsync(
                    RunnerCapabilityProbe.Advertise(_options, gitPushReady: false),
                    RunnerCapabilityProbe.Telemetry(telemetry.SampleIfDue(active.Count)),
                    ++capabilityGeneration,
                    shutdown);
                nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
            }
            var claimedAny = false;
            while (active.Count < _options.HostMaxParallelism && !shutdown.IsCancellationRequested)
            {
                ReviewClaimResponse claim;
                try
                {
                    claim = await _client.ClaimReviewAsync(
                        new ReviewClaimRequest(
                            _options.RunnerId,
                            _client.RunnerInstanceId,
                            _options.TtlSeconds,
                            _options.HostMaxParallelism - active.Count),
                        shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (TaskServerException fatal) when (fatal.StatusCode is 401 or 403)
                {
                    // Auth/registration is definitively broken - absorbing this
                    // would spin a silent forever-loop. Exit; systemd restarts
                    // the daemon, which re-registers with a fresh identity.
                    _log($"review claim poll rejected with {fatal.StatusCode}; exiting for re-registration: {fatal.Message}");
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                                                  || !shutdown.IsCancellationRequested)
                {
                    // A failed claim poll must never kill the daemon. Even with
                    // durable workers, repeated restarts would churn adoption and
                    // can interrupt workspace preparation before worker launch.
                    // Absorb like the coding daemon and retry on the next poll.
                    _log($"review claim poll failed; retrying next tick: {exception.Message}");
                    break;
                }
                if (!string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase))
                    break;
                // In-flight dedup: after a lease died mid-run (renew outage), the
                // server hands the same attempt out again with a fresh fence. This
                // very process may still be executing it - starting a second
                // executor would double-run the review and discard the first via
                // StaleFence. Skip; the running slot finishes or dies first.
                if (active.Any(slot => string.Equals(slot.AttemptId, claim.Attempt!.AttemptId, StringComparison.Ordinal)))
                {
                    _log($"claim returned attempt {claim.Attempt!.AttemptId} already in flight on this host; skipping duplicate execution");
                    break;
                }
                claimedAny = true;
                _log($"claimed remote review attempt={claim.Attempt!.AttemptId} subject={claim.Subject!.SubjectId} slot={active.Count + 1}/{_options.HostMaxParallelism}");
                var executor = new RemoteReviewExecutor(_options, _client, state, _log);
                var stale = state.Find(claim.Attempt!.AttemptId);
                if (stale is not null
                    && !DurableReviewProcess.HasCompleted(stale)
                    && !DurableReviewProcess.VerifyLive(stale, out var adoptionReason))
                {
                    // The old fence may have expired before its loss report was
                    // accepted. Rebind only the report authority to the freshly
                    // claimed fence; never execute from an unproven old process.
                    stale = state.Save(stale with
                    {
                        Claim = claim,
                        Phase = "adoption-failed-reclaimed",
                        AdoptionFailure = adoptionReason,
                    });
                    active.Add((
                        executor.ReportNonAdoptableAsync(stale, adoptionReason, shutdown),
                        claim.Attempt.AttemptId));
                }
                else
                {
                    active.Add((
                        executor.RunClaimedAsync(claim, shutdown),
                        claim.Attempt.AttemptId));
                }
            }
            if (!claimedAny)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    // Exit below after durable review tasks observe the handoff.
                }
            }
        }
        state.Flush();
        if (active.Count > 0)
            await Task.WhenAll(active.Select(slot => slot.Run));
        _log("review daemon drain complete; durable review workers are ready for replacement adoption");
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
}
