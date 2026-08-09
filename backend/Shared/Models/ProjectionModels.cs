using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Severity classification carried by structured chat events. The frontend
/// uses it to pick a bubble accent (info / warn / error) without re-parsing
/// the body. Kept narrow on purpose; richer scoring belongs in metadata.
/// </summary>
public enum ProjectedEventSeverity
{
    Info,
    Warn,
    Error
}

/// <summary>
/// Single event in the projected conversation. The body is already-rendered
/// HTML (sanitized + image-rewritten). Structured metadata (kind, role, refs,
/// severity) lets the client filter, theme, and group without re-parsing.
/// See F22 prompt; matches <c>frontend/.../chat/conversation-event.ts</c>
/// well enough to swap renderer wiring without changing existing CSS.
/// </summary>
public sealed record ProjectedEvent
{
    /// <summary>Stable id; same input produces the same id so the client can dedupe deltas.</summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Event kind, e.g. <c>message.user</c>, <c>message.taskAgent</c>,
    /// <c>message.orchestrator</c>, <c>toolBurst</c>, <c>system.captureFail</c>,
    /// <c>system.parserWarning</c>, <c>workbench.aspectVerdict</c>, <c>runMarker</c>.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>UTC; the merge sort key.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>
    /// Which <see cref="IConversationEventSource"/> emitted this event:
    /// <c>cli</c>, <c>orchestrator</c>, <c>auto-review</c>, <c>runner-event</c>,
    /// <c>system</c>.
    /// </summary>
    public string SourceKind { get; init; } = "";

    /// <summary><c>user</c>, <c>agent</c>, <c>orchestrator</c>, <c>supervisor</c>, or null for system rows.</summary>
    public string? Role { get; init; }

    /// <summary>Already-rendered HTML; safe to drop into <c>[innerHTML]</c>.</summary>
    public string BodyHtml { get; init; } = "";

    /// <summary>Single-line plain-text preview for collapsed cards / tooltips.</summary>
    public string? Summary { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProjectedEventSeverity? Severity { get; init; }

    /// <summary>Cross-references such as run ids, aspect ids, file paths. Used for filtering.</summary>
    public IReadOnlyList<string>? Refs { get; init; }

    /// <summary>Kind-specific structured data the renderer may need (e.g. tool family counts).</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Inputs every source produces; the projector turns these into <see cref="ProjectedEvent"/>
/// after running markdown + sanitize + image rewrite over <see cref="BodyMarkdown"/>.
/// Keeping the raw markdown on this struct (rather than HTML) means the renderer
/// stays the single place where Markdig + Ganss runs.
/// </summary>
public sealed record RawSourceEvent
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime TimestampUtc { get; init; }
    public string SourceKind { get; init; } = "";
    public string? Role { get; init; }
    public string BodyMarkdown { get; init; } = "";
    public string? Summary { get; init; }
    public ProjectedEventSeverity? Severity { get; init; }
    public IReadOnlyList<string>? Refs { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Per-projection context for the task whose events are being rendered.
/// Carries enough to resolve relative image paths against the right API URL.
/// </summary>
public sealed record ImageContext
{
    public string JobId { get; init; } = "";
    public string? WatchPath { get; init; }
}

/// <summary>
/// Knobs that callers may flip per request. <see cref="SinceUtc"/> drives the
/// optional <c>?sinceMs=</c> query parameter for incremental fetches.
/// </summary>
public sealed record ProjectionOptions
{
    public DateTime? SinceUtc { get; init; }

    public static ProjectionOptions Default { get; } = new();
}
