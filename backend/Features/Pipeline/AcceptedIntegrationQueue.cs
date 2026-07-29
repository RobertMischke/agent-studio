using System.Threading.Channels;

namespace AgentStudio.Pipeline;

/// <summary>
/// One accepted delivery whose merge, pre-develop gate, and integration push
/// hand-off must run outside the accept HTTP request.
/// </summary>
public sealed record AcceptedIntegrationRequest(
    string Project,
    string JobId,
    string JobFolderPath,
    string? WatchPath,
    string IntegrationBranch,
    string IntegrationStrategy,
    int? CompletedLaneIndex = null,
    string? Cause = null,
    string? Reason = null);

/// <summary>
/// Volatile latency hand-off for transactional acceptance integration. Human
/// Review plus the integrating phase, pending pipeline step, and
/// <c>integrationpending</c> tag are the durable facts;
/// <see cref="AcceptedIntegrationBackstopHostedService"/> reconstructs work
/// after a restart.
///
/// <para>
/// One reader preserves acceptance order and complements
/// <see cref="MergeIntoDevelopRunner"/>'s serialized merge/gate boundary. The
/// channel is unbounded because volume is one small item per accepted task.
/// </para>
/// </summary>
public sealed class AcceptedIntegrationQueue
{
    private readonly Channel<AcceptedIntegrationRequest> _channel =
        Channel.CreateUnbounded<AcceptedIntegrationRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<AcceptedIntegrationRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues without blocking. A false result means shutdown completed the
    /// channel; the durable accepted-integration backstop will recover the item.
    /// </summary>
    public bool Enqueue(AcceptedIntegrationRequest request) => _channel.Writer.TryWrite(request);
}
