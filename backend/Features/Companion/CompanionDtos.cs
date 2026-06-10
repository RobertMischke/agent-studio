using System.Text.Json.Serialization;

namespace AgentStudio.Companion;

/// <summary>
/// Shape pushed to the relay on every tick. Mirrors the relay's
/// <c>CompanionSnapshot</c> envelope but the <c>Payload</c> here is strongly
/// typed so the snapshot builder can be unit-tested.
/// </summary>
public sealed record CompanionSnapshotEnvelope
{
    [JsonPropertyName("snapshotAt")] public DateTimeOffset SnapshotAt { get; init; }
    [JsonPropertyName("host")] public CompanionHost Host { get; init; } = new();
    [JsonPropertyName("payload")] public CompanionPayload Payload { get; init; } = new();
}

public sealed record CompanionHost
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("isDev")] public bool IsDev { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
}

public sealed record CompanionPayload
{
    [JsonPropertyName("projects")] public List<CompanionProject> Projects { get; init; } = new();
    [JsonPropertyName("tokens")] public CompanionTokens Tokens { get; init; } = new();
    [JsonPropertyName("quota")] public List<CompanionQuotaWindow> Quota { get; init; } = new();
}

public sealed record CompanionProject
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("watchPath")] public string WatchPath { get; init; } = "";
    [JsonPropertyName("runner")] public CompanionRunner Runner { get; init; } = new();
    [JsonPropertyName("pipeline")] public CompanionPipeline Pipeline { get; init; } = new();
}

public sealed record CompanionRunner
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "manual";
    [JsonPropertyName("activeJobId")] public string? ActiveJobId { get; init; }
}

public sealed record CompanionPipeline
{
    [JsonPropertyName("ready")] public List<CompanionJobCard> Ready { get; init; } = new();
    [JsonPropertyName("progress")] public List<CompanionJobCard> Progress { get; init; } = new();
    [JsonPropertyName("review")] public List<CompanionJobCard> Review { get; init; } = new();
}

public sealed record CompanionJobCard
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("agent")] public string Agent { get; init; } = "";
    [JsonPropertyName("model")] public string? Model { get; init; }
}

public sealed record CompanionTokens
{
    [JsonPropertyName("totalCalls")] public int TotalCalls { get; init; }
    [JsonPropertyName("inputTokens")] public long InputTokens { get; init; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; init; }
    [JsonPropertyName("cacheReadTokens")] public long CacheReadTokens { get; init; }
    [JsonPropertyName("cacheCreateTokens")] public long CacheCreateTokens { get; init; }
}

public sealed record CompanionQuotaWindow
{
    [JsonPropertyName("cli")] public string Cli { get; init; } = "";
    [JsonPropertyName("window")] public string Window { get; init; } = "";
    [JsonPropertyName("usedPct")] public double? UsedPct { get; init; }
    [JsonPropertyName("resetsAt")] public DateTimeOffset? ResetsAt { get; init; }
    [JsonPropertyName("plan")] public string? Plan { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

// --- Wire formats for the relay leg ----------------------------------------

public sealed record CompanionSyncRequest
{
    [JsonPropertyName("snapshot")] public CompanionSnapshotEnvelope Snapshot { get; init; } = new();
    [JsonPropertyName("ackIds")] public List<string> AckIds { get; init; } = new();
}

public sealed record CompanionSyncResponse
{
    [JsonPropertyName("commands")] public List<CompanionRelayCommand> Commands { get; init; } = new();
}

/// <summary>Command pulled from the relay. <c>Payload</c> is deserialised by kind in <see cref="CompanionCommandDispatcher"/>.</summary>
public sealed record CompanionRelayCommand
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("payload")] public System.Text.Json.JsonElement Payload { get; init; }
}

// --- Per-kind command payloads ---------------------------------------------

public sealed record DecisionAnswerPayload
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
    [JsonPropertyName("watchPath")] public string WatchPath { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    /// <summary>One of <c>continue</c>, <c>steer</c>, <c>extend</c>, <c>newTask</c>. Defaults to <c>continue</c>.</summary>
    [JsonPropertyName("mode")] public string? Mode { get; init; }
}

public sealed record NewTaskPayload
{
    [JsonPropertyName("watchPath")] public string WatchPath { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("agent")] public string Agent { get; init; } = "claude";
    [JsonPropertyName("cliType")] public string? CliType { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
}

public sealed record StartJobPayload
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
    [JsonPropertyName("watchPath")] public string WatchPath { get; init; } = "";
}
