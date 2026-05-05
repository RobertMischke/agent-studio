using System.Text.Json.Serialization;

namespace OrchestratorApi.Models;

/// <summary>
/// One record on the Agent Message Bus. Mirrors
/// <c>docs/schemas/agent-message.schema.json</c>. Append-only on disk, immutable
/// once written. The bus is observability and reference; it does not move jobs.
/// </summary>
/// <remarks>
/// Enum-like fields stay as plain <see cref="string"/> on the record so the
/// schema's mixed casing (PascalCase severity, lowercase role, kebab-case kind
/// and artifact kind) survives a round-trip without per-field converters.
/// Validation against the allowed value sets lives in
/// <see cref="OrchestratorApi.Services.Bus.AgentMessageValidator"/>.
/// </remarks>
public sealed record AgentMessage
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("participantId")]
    public required string ParticipantId { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("cliSessionId")]
    public string? CliSessionId { get; init; }

    [JsonPropertyName("topic")]
    public string? Topic { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("replyToId")]
    public string? ReplyToId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("tokens")]
    public AgentMessageTokens? Tokens { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<AgentArtifactRef>? Artifacts { get; init; }

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record AgentMessageTokens(
    [property: JsonPropertyName("input")] long Input,
    [property: JsonPropertyName("output")] long Output,
    [property: JsonPropertyName("cacheRead")] long? CacheRead = null,
    [property: JsonPropertyName("cacheWrite")] long? CacheWrite = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("dollars")] double? Dollars = null);

public sealed record AgentArtifactRef
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("byteRange")]
    public AgentArtifactByteRange? ByteRange { get; init; }

    [JsonPropertyName("lineRange")]
    public AgentArtifactLineRange? LineRange { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }
}

public sealed record AgentArtifactByteRange(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

public sealed record AgentArtifactLineRange(
    [property: JsonPropertyName("start")] long Start,
    [property: JsonPropertyName("end")] long End);

public sealed record AgentParticipant
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("cli")]
    public string? Cli { get; init; }

    [JsonPropertyName("skill")]
    public string? Skill { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; init; }
}

/// <summary>
/// Filter passed to <c>AgentMessageBusStore.Query</c>. All fields are optional;
/// null means "do not filter on this dimension".
/// </summary>
public sealed record AgentMessageQuery(
    string? JobId = null,
    string? RunId = null,
    string? ParticipantId = null,
    string? Kind = null,
    string? Severity = null,
    string? Cli = null,
    string? Skill = null,
    string? Tag = null,
    string? CorrelationId = null,
    DateTime? Since = null,
    DateTime? Until = null,
    int? Limit = null);

/// <summary>
/// Aggregate counters for one project's bus, returned by the summary endpoint.
/// </summary>
public sealed record AgentMessageSummary(
    string Project,
    int TotalMessages,
    DateTime? FirstMessageAt,
    DateTime? LastMessageAt,
    IReadOnlyDictionary<string, int> CountsByKind,
    IReadOnlyDictionary<string, int> CountsByParticipant,
    IReadOnlyDictionary<string, int> CountsBySeverity);
