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
    private readonly RunnerProcessInventoryTracker? _inventory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public LeaseHeartbeat(
        TaskServerClient client,
        RunnerOptions options,
        RunLeaseInfoDto lease,
        Action<string> log,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        RunnerProcessInventoryTracker? inventory = null)
    {
        _client = client;
        _options = options;
        _lease = lease;
        _log = log;
        _inventory = inventory;
        _delay = delay ?? Task.Delay;
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
        var authorityExpiresAt = _lease.ExpiresAt;
        try
        {
            while (!stopRun.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                RunLeaseResponse resp;
                try
                {
                    var inventory = _inventory?.Snapshot();
                    var req = new RunLeaseHeartbeatRequest(
                        _lease.TaskKey, _lease.LeaseId, _lease.FencingToken, _options.RunnerId, _options.TtlSeconds,
                        _lease.AttemptId, _lease.AuthorityEpoch,
                        $"heartbeat:{_lease.AttemptId}:{Guid.NewGuid():N}",
                        inventory);
                    resp = await _client.RenewLeaseAsync(req, shutdown);
                    if (_client.UsesDurableTaskServer && inventory is not null)
                        _inventory!.AcknowledgeReports(inventory);
                }
                catch (TaskServerException ex) when (IsDefinitiveLeaseRejection(ex))
                {
                    MarkLeaseLost(
                        stopRun,
                        $"Task Server rejected lease renewal with HTTP {ex.StatusCode}: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    // A transient network error is not proof of a takeover while
                    // the last acknowledged lease window is still open. Once that
                    // window closes, continuing the process would be an unfenced
                    // split brain, so fail closed and reap it.
                    if (DateTime.UtcNow >= authorityExpiresAt)
                    {
                        MarkLeaseLost(
                            stopRun,
                            $"renewal could not be confirmed before lease expiry {authorityExpiresAt:o}: {ex.Message}");
                        return;
                    }
                    _log($"heartbeat error (will retry): {ex.Message}");
                    await _delay(interval, stopRun.Token);
                    continue;
                }

                if (!resp.Granted)
                {
                    MarkLeaseLost(stopRun, $"{resp.Outcome} - {resp.Message}");
                    return;
                }
                if (resp.Lease is not null)
                    authorityExpiresAt = resp.Lease.ExpiresAt;
                _inventory?.Apply(resp.ReconciliationActions);
                await _delay(interval, stopRun.Token);
            }
        }
        catch (OperationCanceledException) { /* run finished or shutting down */ }
    }

    internal static bool IsDefinitiveLeaseRejection(TaskServerException ex)
        => ex.StatusCode is >= 400 and < 500
           && ex.StatusCode is not 408 and not 429;

    private void MarkLeaseLost(CancellationTokenSource stopRun, string reason)
    {
        LeaseLost = true;
        _log($"lease lost; terminating CLI process group: {reason}");
        stopRun.Cancel();
    }
}
