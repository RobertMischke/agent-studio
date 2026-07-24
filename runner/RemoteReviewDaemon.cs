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
            active.RemoveAll(task => task.IsCompleted);
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
