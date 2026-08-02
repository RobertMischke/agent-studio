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
        var active = new List<(Task<int> Run, string AttemptId)>();
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? latestTelemetry = null;
        var connectivity = new TaskServerConnectivityMonitor(_log);
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
            catch (Exception exception)
            {
                _log($"host-telemetry-sample-failed error={exception.GetType().Name} message={exception.Message}");
                return null;
            }
        }
        await WithServerRetryAsync(
            "review registration",
            () => _client.RegisterAsync(_options.RunnerName, "review-executor", shutdown),
            connectivity,
            () => active.Count,
            shutdown);
        var capabilityGeneration = DateTime.UtcNow.Ticks;
        await WithServerRetryAsync<object?>(
            "review capability advertisement",
            async () =>
            {
                await _client.AdvertiseCapabilitiesAsync(
                    RunnerCapabilityProbe.Advertise(
                        _options,
                        gitPushReady: false,
                        connectivity: connectivity.Snapshot),
                    RunnerCapabilityProbe.Telemetry(TakeTelemetry(force: true)),
                    capabilityGeneration,
                    shutdown);
                return null;
            },
            connectivity,
            () => active.Count,
            shutdown);
        var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
        var consecutiveFaults = 0;
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
            try
            {
                if (DateTime.UtcNow >= nextCapabilityAdvertisement)
                {
                    await _client.AdvertiseCapabilitiesAsync(
                        RunnerCapabilityProbe.Advertise(
                            _options,
                            gitPushReady: false,
                            connectivity: connectivity.Snapshot),
                        RunnerCapabilityProbe.Telemetry(TakeTelemetry()),
                        ++capabilityGeneration,
                        shutdown);
                    nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
                }
                var claimedAny = false;
                while (active.Count < _client.HostMaxParallelism && !shutdown.IsCancellationRequested)
                {
                    var claim = await _client.ClaimReviewAsync(
                            new ReviewClaimRequest(
                                _options.RunnerId,
                                _client.RunnerInstanceId,
                                _options.TtlSeconds,
                                _client.HostMaxParallelism - active.Count),
                            shutdown);
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
                    _log($"claimed remote review attempt={claim.Attempt!.AttemptId} subject={claim.Subject!.SubjectId} slot={active.Count + 1}/{_client.HostMaxParallelism}");
                    active.Add((new RemoteReviewExecutor(_options, _client, _log)
                        .RunClaimedAsync(claim, shutdown), claim.Attempt!.AttemptId));
                }

                if (!claimedAny)
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                consecutiveFaults = 0;
                if (connectivity.RecordSuccess(DateTime.UtcNow, "review claim poll"))
                {
                    TakeTelemetry(force: true);
                    nextCapabilityAdvertisement = DateTime.MinValue;
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // The loop condition ends the daemon after active reviews have
                // recorded their interruption through the fenced cleanup path.
            }
            catch (TaskServerException fatal) when (fatal.StatusCode is 401 or 403)
            {
                _log($"review claim poll rejected with {fatal.StatusCode}; exiting for re-registration: {fatal.Message}");
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
                // Preserve the existing resilience for non-transport conflicts.
                // They are not labelled as route outages because the server did
                // answer and needs a different diagnosis.
                _log($"review claim poll failed; retrying next tick: {exception.Message}");
                await DelayThroughShutdown(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
            }
        }
        if (active.Count > 0)
            await Task.WhenAll(active.Select(slot => slot.Run));
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
                var delay = TaskServerConnectivityMonitor.RetryDelay(_options.PollSeconds, attempt);
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

    private static async Task DelayThroughShutdown(TimeSpan delay, CancellationToken shutdown)
    {
        try { await Task.Delay(delay, shutdown); }
        catch (OperationCanceledException) { }
    }
}
