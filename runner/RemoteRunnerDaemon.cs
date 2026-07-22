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
/// blip must never terminate the daemon: exiting would let systemd kill the whole
/// service cgroup - every in-flight slot with it - and strand each run's lease
/// until its TTL, which surfaces as leases held by no process. So transient
/// connectivity faults are absorbed here (retry with backoff) instead of bubbling
/// up to the process's fatal exit-4 handler.
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

    private TimeSpan BackoffFor(int attempt)
    {
        // Base the backoff on the operator's poll cadence and grow it modestly so a
        // longer outage is not hammered, capped so recovery is still prompt.
        var seconds = Math.Min(_options.PollSeconds * Math.Min(attempt, 6), 60);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    /// <summary>
    /// Run a Task Server call, absorbing transient connectivity faults with a
    /// bounded backoff until it succeeds or shutdown is requested. Used for the
    /// one-time startup calls so a server that is briefly down at boot no longer
    /// costs a fatal exit and a systemd restart cycle.
    /// </summary>
    private async Task<T> WithServerRetryAsync<T>(string what, Func<Task<T>> call, CancellationToken shutdown)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                return await call();
            }
            catch (Exception ex) when (IsTransientServerFault(ex) && !shutdown.IsCancellationRequested)
            {
                var delay = BackoffFor(attempt);
                _log($"task server unreachable during {what} ({ex.Message}); retry {attempt} in {delay.TotalSeconds:0}s");
                await Task.Delay(delay, shutdown);
            }
        }
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        var clientId = await WithServerRetryAsync(
            "runner registration",
            () => _client.RegisterAsync(_options.RunnerName, "service", shutdown),
            shutdown);
        _log($"authenticated daemon '{_options.RunnerName}' with attribution '{clientId}'; slots={_options.HostMaxParallelism}");

        var gitCapability = await GitPushProbe.RunAsync(_options, _log, shutdown);
        await WithServerRetryAsync<object?>(
            "git-capability report",
            async () =>
            {
                await _client.ReportGitCapabilityAsync(clientId, new RunnerGitCapabilityRequest(
                    gitCapability.CanPush ? "ready" : "read-only", gitCapability.Detail, DateTime.UtcNow), shutdown);
                return null;
            },
            shutdown);
        _log($"runner-git-capability status={(gitCapability.CanPush ? "ready" : "read-only")} detail={gitCapability.Detail}");
        if (!gitCapability.CanPush)
            throw new InvalidOperationException("Git push capability is read-only; refusing to claim work until the host credential is repaired.");

        var active = new List<Task<int>>();
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? TakeTelemetry()
        {
            try { return telemetry.SampleIfDue(active.Count); }
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
                if (!active[i].IsCompleted) continue;
                try { _log($"slot completed with exit code {await active[i]}"); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception ex) { _log($"slot failed: {ex}"); }
                active.RemoveAt(i);
            }

            try
            {
                var claimedAny = false;
                if (active.Count >= _options.HostMaxParallelism)
                {
                    var sample = TakeTelemetry();
                    if (sample is not null)
                        _ = await _client.ClaimAsync(new RunnerClaimRequest(
                            _options.RunnerId, _options.RunnerName, _options.Hostname,
                            Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
                            sample, AvailableSlots: 0), shutdown);
                }
                while (active.Count < _options.HostMaxParallelism && !shutdown.IsCancellationRequested)
                {
                    var claim = await _client.ClaimAsync(new RunnerClaimRequest(
                        _options.RunnerId, _options.RunnerName, _options.Hostname,
                        Environment.ProcessId, _options.BackendName, _options.TtlSeconds,
                        TakeTelemetry()), shutdown);
                    if (claim.Status != RunnerClaimStatus.Claimed
                        || string.IsNullOrWhiteSpace(claim.TaskKey)
                        || claim.Lease is null)
                        break;

                    claimedAny = true;
                    _log($"claimed {claim.ProjectName}/{claim.TaskKey} using project cache {claim.ProjectId ?? "legacy fallback"} into slot {active.Count + 1}/{_options.HostMaxParallelism}");
                    var taskRunner = new RemoteTaskRunner(_options, _client, _log);
                    active.Add(taskRunner.RunClaimedAsync(
                        claim.TaskKey,
                        claim.Lease,
                        shutdown,
                        claim.ProjectId,
                        claim.RepositoryUrl,
                    claim.DefaultBranch,
                    claim.TaskKind));
                }

                if (!claimedAny)
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
                consecutiveFaults = 0;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Clean shutdown: fall through so the loop condition ends it and
                // the drain below awaits the in-flight slots.
            }
            catch (Exception ex) when (IsTransientServerFault(ex))
            {
                // The Task Server blipped while we polled for work. This is the exact
                // fault that used to bubble to Program.cs and exit the process with
                // code 4 - killing the whole cgroup and stranding leases. Instead:
                // keep every in-flight slot running (their heartbeats tolerate the
                // same blip) and retry the claim after a bounded backoff.
                var delay = BackoffFor(++consecutiveFaults);
                _log($"task server unreachable while claiming work ({ex.Message}); " +
                     $"{active.Count} slot(s) still running; retry {consecutiveFaults} in {delay.TotalSeconds:0}s");
                await DelayThroughShutdown(delay, shutdown);
            }
        }

        if (active.Count > 0)
        {
            try { await Task.WhenAll(active); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
    }

    private static async Task DelayThroughShutdown(TimeSpan delay, CancellationToken shutdown)
    {
        try { await Task.Delay(delay, shutdown); }
        catch (OperationCanceledException) { /* shutting down; the loop condition ends it */ }
    }
}
