using System.Diagnostics;

namespace AgentStudio.Runner;

/// <summary>
/// Drains <see cref="CompletedPushQueue"/> and performs the completed-job
/// auto-push off the HTTP request path. One reader, processed strictly in
/// order. Failures are logged and swallowed: the periodic
/// <see cref="CompletedPushBackstopHostedService"/> retries anything that did
/// not land (transient network error, divergent remote, or an item still in
/// the channel at shutdown).
/// </summary>
public sealed class CompletedPushWorker : BackgroundService
{
    private readonly CompletedPushQueue _queue;
    private readonly TaskTransitionService _transitions;
    private readonly ILogger<CompletedPushWorker> _logger;

    public CompletedPushWorker(
        CompletedPushQueue queue,
        TaskTransitionService transitions,
        ILogger<CompletedPushWorker> logger)
    {
        _queue = queue;
        _transitions = transitions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(__ex, "CompletedPushWorker: Graceful shutdown. Unpushed items are recovered by the backstop.");
            // Graceful shutdown. Unpushed items are recovered by the backstop.
        }
    }

    /// <summary>
    /// Pushes one queued completed job. Exposed so tests can drain the queue
    /// deterministically without standing up the background loop; production
    /// reaches it only through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task ProcessAsync(CompletedPushRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var pushed = await _transitions.PushCompletedJobCommitsAsync(request.Job, request.Strategy, ct);
            sw.Stop();
            if (pushed > 0)
            {
                _logger.LogInformation(
                    "Completed-push worker pushed {Count} commit(s) for {JobId} in {ElapsedMs}ms",
                    pushed, request.Job.Id, sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Completed-push worker failed for {JobId}; backstop will retry", request.Job.Id);
        }
    }
}
