using System.Threading.Channels;

namespace AgentStudio.Pipeline;

public sealed record WorkspaceArtifactPushRequest(string RepositoryRoot, string JobId);

/// <summary>Non-blocking hand-off from workspace commits to the remote push worker.</summary>
public sealed class WorkspaceArtifactPushQueue
{
    private readonly Channel<WorkspaceArtifactPushRequest> _channel =
        Channel.CreateUnbounded<WorkspaceArtifactPushRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<WorkspaceArtifactPushRequest> Reader => _channel.Reader;
    public bool Enqueue(WorkspaceArtifactPushRequest request) => _channel.Writer.TryWrite(request);
}
