namespace AgentStudio.Projection;

/// <summary>
/// One pluggable producer of <see cref="RawSourceEvent"/>s for a given job.
/// Sources are stateless; cursors stay on the caller side so we can scale to
/// many concurrent jobs without per-source bookkeeping.
/// </summary>
public interface IConversationEventSource
{
    /// <summary>Short identifier; matches the corresponding <see cref="RawSourceEvent.SourceKind"/>.</summary>
    string SourceKind { get; }

    /// <summary>
    /// Read all events this source can see for <paramref name="jobInfo"/>.
    /// Implementations should not throw on a missing file (return empty);
    /// I/O errors are the source's responsibility to log and absorb.
    /// </summary>
    Task<IReadOnlyList<RawSourceEvent>> ReadAsync(
        AgentStudio.Shared.TaskInfo jobInfo,
        CancellationToken ct);

    /// <summary>
    /// Returns the file (or directory) modification time the projector should
    /// hash into the cache key. <c>DateTime.MinValue</c> when nothing on disk
    /// matters or the source is empty for this job.
    /// </summary>
    DateTime GetSourceMTimeUtc(AgentStudio.Shared.TaskInfo jobInfo);
}
