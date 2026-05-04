using System.Text.Json.Serialization;

namespace CompanionRelay;

/// <summary>
/// Snapshot the local processor pushes on every sync tick. The relay stores
/// the last one in memory and serves it back to the PWA. Full state, not a
/// delta. The relay does not interpret any of the contents; it is opaque
/// JSON wrapped in a thin envelope so the relay can show liveness without
/// understanding the schema.
/// </summary>
public sealed record CompanionSnapshot
{
    [JsonPropertyName("snapshotAt")]
    public DateTimeOffset SnapshotAt { get; init; }

    [JsonPropertyName("host")]
    public CompanionHost Host { get; init; } = new();

    /// <summary>
    /// Opaque payload the processor builds and the PWA renders. Kept as a
    /// JsonElement so the relay version is independent of the snapshot schema.
    /// </summary>
    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement Payload { get; init; }
}

public sealed record CompanionHost
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("isDev")] public bool IsDev { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
}

public sealed record CompanionCommand
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("payload")] public System.Text.Json.JsonElement Payload { get; init; }
}

public sealed record SyncRequest
{
    [JsonPropertyName("snapshot")] public CompanionSnapshot Snapshot { get; init; } = new();

    /// <summary>
    /// Ids of commands the processor has finished applying since the last
    /// sync. The relay drops these from its queue.
    /// </summary>
    [JsonPropertyName("ackIds")] public List<string> AckIds { get; init; } = new();
}

public sealed record SyncResponse
{
    [JsonPropertyName("commands")] public List<CompanionCommand> Commands { get; init; } = new();
}

public sealed record EnqueueCommandRequest
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("payload")] public System.Text.Json.JsonElement Payload { get; init; }
}

public sealed record EnqueueCommandResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
}

public sealed record StateResponse
{
    [JsonPropertyName("snapshot")] public CompanionSnapshot? Snapshot { get; init; }
    [JsonPropertyName("lastSyncAt")] public DateTimeOffset? LastSyncAt { get; init; }
    [JsonPropertyName("pendingCommandCount")] public int PendingCommandCount { get; init; }
}

public sealed record HealthResponse
{
    [JsonPropertyName("status")] public string Status { get; init; } = "ok";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("lastSyncAt")] public DateTimeOffset? LastSyncAt { get; init; }
}
