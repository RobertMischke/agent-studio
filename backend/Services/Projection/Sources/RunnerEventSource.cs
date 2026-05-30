using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Projection.Sources;

/// <summary>
/// Runner state changes (move into/out of 3-progress, watchdog kills,
/// capture-fail) projected as <c>runMarker</c> / <c>supervisor.wait</c>
/// events.
///
/// Most of this information is currently echoed onto the CLI log
/// <c>[orchestrator]</c> stream and <see cref="CliOutputSource"/> picks it
/// up there. This source is a registered no-op until a dedicated runner
/// event journal lands. Keeping the contract live lets the projector
/// already account for the source in its mtime tuple.
/// </summary>
public sealed class RunnerEventSource : IConversationEventSource
{
    public string SourceKind => "runner-event";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(TaskInfo jobInfo, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RawSourceEvent>>(Array.Empty<RawSourceEvent>());

    public DateTime GetSourceMTimeUtc(TaskInfo jobInfo) => DateTime.MinValue;
}
