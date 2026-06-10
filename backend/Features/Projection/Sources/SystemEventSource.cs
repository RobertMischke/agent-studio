
namespace AgentStudio.Projection;

/// <summary>
/// Parser warnings, schema drifts, and taskboard-level system messages.
/// Today these flow through the CLI log under <c>[orchestrator]</c> and
/// <see cref="CliOutputSource"/> already produces
/// <c>system.parserWarning</c> / <c>system.schemaDrift</c> for them. This
/// source is a registered no-op until a dedicated journal lands; the
/// projector still iterates over it so the contract is in place.
/// </summary>
public sealed class SystemEventSource : IConversationEventSource
{
    public string SourceKind => "system";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(TaskInfo jobInfo, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RawSourceEvent>>(Array.Empty<RawSourceEvent>());

    public DateTime GetSourceMTimeUtc(TaskInfo jobInfo) => DateTime.MinValue;
}
