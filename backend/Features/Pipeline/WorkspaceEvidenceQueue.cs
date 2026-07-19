using System.Threading.Channels;

namespace AgentStudio.Pipeline;

/// <summary>
/// One lane transition that wants its workspace evidence committed. Enqueued by
/// <see cref="AgentStudio.Tasks.TaskStateMachine"/> after a successful move —
/// never committed synchronously in the request path. <see cref="WatchPath"/>
/// is the project storage root that owns the moved task; the rest is best-effort
/// labelling for the batched commit message.
/// </summary>
public sealed record WorkspaceEvidenceRequest(
    string WatchPath,
    string ProjectName,
    string Slug,
    string FromState,
    string ToState);

/// <summary>
/// Non-blocking hand-off from lane transitions to the debounced
/// Transition-Committer worker. Unbounded and multi-writer: enqueue happens on
/// whichever thread performed the move (operator API, runner pickup, boot
/// sweep), the single reader is <see cref="WorkspaceEvidenceWorker"/>.
/// </summary>
public sealed class WorkspaceEvidenceQueue
{
    private readonly Channel<WorkspaceEvidenceRequest> _channel =
        Channel.CreateUnbounded<WorkspaceEvidenceRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<WorkspaceEvidenceRequest> Reader => _channel.Reader;

    /// <summary>Best-effort, never throws; a dropped write only loses a nudge,
    /// the boot catch-up and later transitions still capture the drift.</summary>
    public bool Enqueue(WorkspaceEvidenceRequest request) => _channel.Writer.TryWrite(request);
}
