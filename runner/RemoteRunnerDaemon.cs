namespace AgentRunner;

/// <summary>
/// Continuously fills a bounded set of host slots from the Task Server's
/// assignment-aware claim endpoint. Each claim already owns a fenced lease and
/// is executed in its own linked git worktree by <see cref="RemoteTaskRunner"/>.
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

    public async Task RunAsync(CancellationToken shutdown)
    {
        var clientId = await _client.RegisterAsync(_options.RunnerName, "service", shutdown);
        _log($"registered daemon '{_options.RunnerName}' as client '{clientId}'; slots={_options.HostMaxParallelism}");

        var gitCapability = await GitPushProbe.RunAsync(_options, _log, shutdown);
        await _client.ReportGitCapabilityAsync(clientId, new RunnerGitCapabilityRequest(
            gitCapability.CanPush ? "ready" : "read-only", gitCapability.Detail, DateTime.UtcNow), shutdown);
        _log($"runner-git-capability status={(gitCapability.CanPush ? "ready" : "read-only")} detail={gitCapability.Detail}");

        var active = new List<Task<int>>();
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

            var claimedAny = false;
            while (active.Count < _options.HostMaxParallelism && !shutdown.IsCancellationRequested)
            {
                var claim = await _client.ClaimAsync(new RunnerClaimRequest(
                    _options.RunnerId, _options.RunnerName, _options.Hostname,
                    Environment.ProcessId, _options.BackendName, _options.TtlSeconds), shutdown);
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
                    claim.DefaultBranch));
            }

            if (!claimedAny)
                await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
        }

        if (active.Count > 0)
        {
            try { await Task.WhenAll(active); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
    }
}
