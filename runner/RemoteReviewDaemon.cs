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
        await _client.RegisterAsync(_options.RunnerName, "review-executor", shutdown);
        var active = new List<(Task<int> Run, string AttemptId)>();
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? TakeTelemetry()
        {
            try
            {
                return telemetry.SampleNow(active.Count);
            }
            catch (Exception exception)
            {
                _log(
                    $"review host telemetry sample failed error={exception.GetType().Name} "
                    + $"message={exception.Message}");
                return null;
            }
        }
        var capabilityGeneration = DateTime.UtcNow.Ticks;
        await _client.AdvertiseCapabilitiesAsync(
            RunnerCapabilityProbe.Advertise(_options, gitPushReady: false),
            RunnerCapabilityProbe.Telemetry(TakeTelemetry()),
            capabilityGeneration,
            shutdown);
        var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
        var admissionClosed = false;
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
            var admissionTelemetry = TakeTelemetry();
            if (DateTime.UtcNow >= nextCapabilityAdvertisement)
            {
                await _client.AdvertiseCapabilitiesAsync(
                    RunnerCapabilityProbe.Advertise(_options, gitPushReady: false),
                    RunnerCapabilityProbe.Telemetry(admissionTelemetry),
                    ++capabilityGeneration,
                    shutdown);
                nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
            }

            var admission = ReviewSlotAdmissionPolicy.Decide(
                admissionTelemetry,
                active.Count,
                _options.HostMaxParallelism,
                _options.ClaimMaxLoadPerCore);
            if (!admission.Admitted)
            {
                if (!admissionClosed)
                    _log($"review slot admission closed: {admission.Reason}; activeSlots={active.Count}");
                admissionClosed = true;
            }
            else
            {
                if (admissionClosed)
                    _log($"review slot admission reopened: {admission.Reason}; activeSlots={active.Count}");
                admissionClosed = false;
                ReviewClaimResponse claim;
                try
                {
                    claim = await _client.ClaimReviewAsync(
                        new ReviewClaimRequest(
                            _options.RunnerId,
                            _client.RunnerInstanceId,
                            _options.TtlSeconds,
                            AvailableSlots: 1),
                        shutdown);
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
                    // A failed claim poll must never kill the daemon: the systemd
                    // restart would abort every in-flight review on this host and
                    // re-claim it from zero (observed as a 409 crash-loop). Absorb
                    // like the coding daemon and retry on the next poll tick.
                    _log($"review claim poll failed; retrying next tick: {exception.Message}");
                    claim = new ReviewClaimResponse("empty");
                }
                if (string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase))
                {
                    // In-flight dedup: after a lease died mid-run (renew outage), the
                    // server hands the same attempt out again with a fresh fence. This
                    // very process may still be executing it - starting a second
                    // executor would double-run the review and discard the first via
                    // StaleFence. Skip; the running slot finishes or dies first.
                    if (active.Any(slot => string.Equals(
                            slot.AttemptId,
                            claim.Attempt!.AttemptId,
                            StringComparison.Ordinal)))
                    {
                        _log($"claim returned attempt {claim.Attempt!.AttemptId} already in flight on this host; skipping duplicate execution");
                    }
                    else
                    {
                        _log($"claimed remote review attempt={claim.Attempt!.AttemptId} subject={claim.Subject!.SubjectId} slot={active.Count + 1}/{_options.HostMaxParallelism}");
                        active.Add((new RemoteReviewExecutor(_options, _client, _log)
                            .RunClaimedAsync(claim, shutdown), claim.Attempt!.AttemptId));
                    }
                }
            }

            // Admit at most one review per telemetry observation. The pause lets
            // the new process affect the next load sample before another lease
            // can enter this host.
            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
        }
        if (active.Count > 0)
            await Task.WhenAll(active.Select(slot => slot.Run));
    }
}
