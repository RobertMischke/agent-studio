namespace AgentStudio.Shared;

/// <summary>
/// Normalized, replay-safe projection of the typed lifecycle events written by
/// a standalone runner. The source journal may carry protocol envelopes, but
/// Agent Studio exposes one stable shape to the frontend so presentation never
/// has to parse runner prose.
/// </summary>
public sealed record RunnerRecordedEvent
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Server-assigned provenance. Null or <c>live</c> means a real runner
    /// produced the event; <c>simulated</c> means it came from the public-demo
    /// replay scope and never represents work that happened. A runner cannot set
    /// this itself on either ingest route.
    /// </summary>
    public string? Origin { get; init; }

    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public int? RunIndex { get; init; }
    public string? Cli { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public long? DurationMs { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public string? Severity { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? ImplementationStatus { get; init; }
    public string? PipelineStatus { get; init; }
}
