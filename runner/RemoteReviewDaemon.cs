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
        var active = new List<Task<int>>();
        while (!shutdown.IsCancellationRequested)
        {
            for (var index = active.Count - 1; index >= 0; index--)
            {
                if (!active[index].IsCompleted) continue;
                try
                {
                    var exitCode = await active[index];
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
            var claimedAny = false;
            while (active.Count < _options.HostMaxParallelism && !shutdown.IsCancellationRequested)
            {
                var claim = await _client.ClaimReviewAsync(
                    new ReviewClaimRequest(
                        _options.RunnerId,
                        _client.RunnerInstanceId,
                        _options.TtlSeconds,
                        _options.HostMaxParallelism - active.Count),
                    shutdown);
                if (!string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase))
                    break;
                claimedAny = true;
                _log($"claimed remote review attempt={claim.Attempt!.AttemptId} subject={claim.Subject!.SubjectId} slot={active.Count + 1}/{_options.HostMaxParallelism}");
                active.Add(new RemoteReviewExecutor(_options, _client, _log)
                    .RunClaimedAsync(claim, shutdown));
            }
            if (!claimedAny)
                await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), shutdown);
        }
        if (active.Count > 0)
            await Task.WhenAll(active);
    }
}
