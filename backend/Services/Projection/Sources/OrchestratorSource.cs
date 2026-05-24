using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Projection.Sources;

/// <summary>
/// Per-job orchestrator chat history → typed events.
///
/// Today most orchestrator activity already flows through the CLI log on
/// the <c>[orchestrator]</c> stream (see <see cref="CliOutputSource"/>), so
/// this source is a registered no-op until a per-job orchestrator history
/// file lands. Keeping the contract live lets us wire the future producer
/// without changing the projector or the endpoint.
/// </summary>
public sealed class OrchestratorSource : IConversationEventSource
{
    public string SourceKind => "orchestrator";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(JobInfo jobInfo, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RawSourceEvent>>(Array.Empty<RawSourceEvent>());

    public DateTime GetSourceMTimeUtc(JobInfo jobInfo) => DateTime.MinValue;
}
