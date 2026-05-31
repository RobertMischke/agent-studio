using System.Threading.Channels;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// One queued completed-job push: the job snapshot (carrying the immutable
/// commit SHAs to push) plus the project's normalized auto-push strategy as it
/// was at enqueue time.
/// </summary>
public sealed record CompletedPushRequest(TaskInfo Job, string Strategy);

/// <summary>
/// Hand-off point that lifts the completed-job <c>git fetch</c> + <c>git push</c>
/// off the move-to-<c>6-completed</c> request path. The network round-trip was
/// being awaited inside the HTTP request, so a "move to complete" took 2-3 s
/// (PERF regression). <see cref="OrchestratorApi.Services.Jobs.TaskTransitionService"/>
/// drops a snapshot here - an instant, non-blocking channel write - and returns;
/// <see cref="CompletedPushWorker"/> drains it and performs the push on a
/// background thread. The periodic <see cref="CompletedPushBackstopHostedService"/>
/// remains the safety net for anything dropped on shutdown or missed before a
/// restart.
/// <para>
/// Unbounded by design: volume is one item per completed move, and SHAs are
/// immutable, so a deferred push is always still correct (it pushes the same
/// commit). A clogged channel can therefore never push the wrong thing; the
/// worst case is a delay the backstop also covers.
/// </para>
/// </summary>
public sealed class CompletedPushQueue
{
    private readonly Channel<CompletedPushRequest> _channel =
        Channel.CreateUnbounded<CompletedPushRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<CompletedPushRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueue a completed-job push. Never blocks. Returns false only if the
    /// channel has been completed (shutdown), in which case the backstop will
    /// pick the commit up on the next sweep.
    /// </summary>
    public bool Enqueue(CompletedPushRequest request) => _channel.Writer.TryWrite(request);
}
