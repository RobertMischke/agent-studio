namespace AgentRunner;

/// <summary>
/// Keeps the fenced run lease alive for the duration of a run. The server clamps
/// the TTL and raises the fencing token on any takeover, so a heartbeat that is
/// rejected as <c>StaleToken</c> or <c>Expired</c> means this runner has lost the
/// lease to another holder and must abandon the run: it cancels the shared token
/// so the CLI is torn down instead of racing the new owner (the §8.2C split-brain
/// guard, enforced runner-side).
/// </summary>
public sealed class LeaseHeartbeat
{
    private readonly TaskServerClient _client;
    private readonly RunnerOptions _options;
    private readonly RunLeaseInfoDto _lease;
    private readonly Action<string> _log;

    public LeaseHeartbeat(TaskServerClient client, RunnerOptions options, RunLeaseInfoDto lease, Action<string> log)
    {
        _client = client;
        _options = options;
        _lease = lease;
        _log = log;
    }

    /// <summary>Set when a heartbeat is rejected: the run must stop, the lease is gone.</summary>
    public bool LeaseLost { get; private set; }

    /// <summary>
    /// Renew on a cadence below the TTL until <paramref name="stopRun"/> fires.
    /// Cancels <paramref name="stopRun"/> itself when the lease is lost so the
    /// caller's run tears down promptly.
    /// </summary>
    public async Task RunAsync(CancellationTokenSource stopRun, CancellationToken shutdown)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatSeconds));
        try
        {
            while (!stopRun.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                await Task.Delay(interval, stopRun.Token);
                RunLeaseResponse resp;
                try
                {
                    var req = new RunLeaseHeartbeatRequest(
                        _lease.TaskKey, _lease.LeaseId, _lease.FencingToken, _options.RunnerId, _options.TtlSeconds,
                        _lease.AttemptId, _lease.AuthorityEpoch,
                        $"heartbeat:{_lease.AttemptId}:{Guid.NewGuid():N}");
                    resp = await _client.RenewLeaseAsync(req, shutdown);
                }
                catch (Exception ex)
                {
                    // A transient network error is not proof of a takeover; log and
                    // let the next tick retry while the TTL still has headroom.
                    _log($"heartbeat error (will retry): {ex.Message}");
                    continue;
                }

                if (!resp.Granted)
                {
                    LeaseLost = true;
                    _log($"lease lost: {resp.Outcome} - {resp.Message}");
                    stopRun.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* run finished or shutting down */ }
    }
}
