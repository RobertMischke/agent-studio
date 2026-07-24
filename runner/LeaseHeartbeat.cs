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
        var authorityExpiresAt = _lease.ExpiresAt.ToUniversalTime();
        var uncertaintyMargin = TimeSpan.FromSeconds(Math.Max(1, interval.TotalSeconds));
        try
        {
            while (!stopRun.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                RunLeaseResponse resp;
                var inventory = _inventory?.Snapshot();
                var request = new RunLeaseHeartbeatRequest(
                    _lease.TaskKey, _lease.LeaseId, _lease.FencingToken, _options.RunnerId, _options.TtlSeconds,
                    _lease.AttemptId, _lease.AuthorityEpoch,
                    $"heartbeat:{_lease.AttemptId}:{Guid.NewGuid():N}",
                    inventory);
                try
                {
                    resp = await _client.RenewLeaseAsync(request, shutdown);
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
                catch (TaskServerException ex) when (
                    ex.StatusCode == 409
                    && _client.UsesHostOrchestrator
                    && !shutdown.IsCancellationRequested)
                {
                    // A Task Server restart preserves the fence but deliberately
                    // marks the process unknown. The matching host instance may
                    // reconcile the same authority; it must never acquire a new
                    // lease or start a duplicate process.
                    try
                    {
                        resp = await _client.ReconcileLeaseAsync(request, shutdown);
                        _log($"lease reconciled after task server restart: {resp.Outcome}");
                    }
                    catch (Exception reconcileError)
                    {
                        _log($"lease reconciliation failed (will retry within offline authority): {reconcileError.Message}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // A transient network error is not proof of a takeover. It
                    // does, however, consume the bounded server-issued authority
                    // window. Stop before the last known expiry minus one renewal
                    // interval so suspend, clock, and transport uncertainty cannot
                    // turn an unreachable Task Server into autonomous execution.
                    var stopBefore = authorityExpiresAt - uncertaintyMargin;
                    if (DateTime.UtcNow >= stopBefore)
                    {
                        MarkLeaseLost(
                            stopRun,
                            "renewal safety boundary reached: task-server-unavailable; " +
                            $"stop-before={stopBefore:o}; cancelling and reaping the active process generation: {ex.Message}");
                        return;
                    }

                    _log($"heartbeat error (will retry before {stopBefore:o}): {ex.Message}");
                    await _delay(interval, stopRun.Token);
                    continue;
                }

                if (!resp.Granted)
                {
                    MarkLeaseLost(stopRun, $"{resp.Outcome} - {resp.Message}");
                    return;
                }
                if (resp.Lease is not null)
                    authorityExpiresAt = resp.Lease.ExpiresAt.ToUniversalTime();
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
