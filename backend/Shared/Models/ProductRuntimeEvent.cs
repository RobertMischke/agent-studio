using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// One structured runtime event emitted by the software the agents are
/// building, mirroring <c>docs/app/schemas/product-runtime-event.schema.json</c>.
/// Append-only on disk, written one JSON document per JSONL line under
/// <c>&lt;job&gt;/logs/runtime/&lt;yyyy-mm-dd&gt;.jsonl</c> (job-scoped) or
/// <c>&lt;workspace&gt;/logs/runtime/&lt;project&gt;/&lt;yyyy-mm-dd&gt;.jsonl</c>
/// (project-scoped). Distinct stream from the Agent Message Bus: bus messages
/// answer "which agent acted, and why"; runtime events answer "what did the
/// built application do, where did it fail, how fast was it".
/// </summary>
public sealed record ProductRuntimeEvent
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("timestamp")]
    public required DateTime Timestamp { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("subsystem")]
    public required string Subsystem { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("duration")]
    public ProductRuntimeEventDuration? Duration { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error")]
    public ProductRuntimeEventError? Error { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

public sealed record ProductRuntimeEventDuration
{
    [JsonPropertyName("ms")]
    public required double Ms { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }
}

public sealed record ProductRuntimeEventError
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }
}

/// <summary>
/// One parse warning recorded when a line in a runtime JSONL file could not be
/// turned into a valid <see cref="ProductRuntimeEvent"/>. The reader keeps the
/// raw line so reviewers can debug malformed producers without rerunning the
/// failing scenario.
/// </summary>
public sealed record RuntimeEventParseWarning(
    string SourcePath,
    int LineNumber,
    string Reason,
    string RawLine);
