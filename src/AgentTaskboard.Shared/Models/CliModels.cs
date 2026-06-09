namespace OrchestratorApi.Models;

public record StartJobRequest
{
    public string? AgentOverride { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
}

public record ContinueJobRequest
{
    public string Prompt { get; init; } = "";
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// How the follow-up should be interpreted. <c>continue</c> (default) is a
    /// next-turn message in the same conversation. <c>steer</c> frames the
    /// follow-up as a course correction. <c>extend</c> appends a new prompt
    /// file to the job folder so the task history grows blog-style.
    /// <c>newTask</c> starts a new sub-task in the same session.
    /// See <see cref="ContinueModes"/>.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Discriminated response for <c>POST /api/tasks/{id}/continue</c> and
/// <c>POST /api/tasks/{id}/start</c>. <c>started</c> means the run is
/// actually live; <c>queued</c> means the project was busy with another
/// job, the user's intent has been saved as a draft on the target task,
/// and the target task has been moved to the top of <c>2-ready</c> so the
/// auto-pickup loop will run it on the next tick. The frontend treats
/// queued as success-with-info (no modal); the chat carries the
/// orchestrator's <c>[queued]</c> meta line for user-facing feedback.
/// </summary>
public record ContinueJobResponse
{
    /// <summary><c>started</c> | <c>queued</c></summary>
    public string Status { get; init; } = "started";
    public CliExecution? Execution { get; init; }
    public ContinueJobQueuedInfo? Queued { get; init; }
}

public record ContinueJobQueuedInfo
{
    /// <summary><c>project-busy</c> is the only reason today.</summary>
    public string Reason { get; init; } = "project-busy";
    /// <summary>The job that was running when the user's send hit; for context only.</summary>
    public string? ActiveJobId { get; init; }
    public string? ActiveJobTitle { get; init; }
    /// <summary>Where in the <c>2-ready</c> queue the target ended up (1 = next pickup).</summary>
    public int Position { get; init; }
    /// <summary>The state the target was in before the queue promotion.</summary>
    public string? PromotedFromState { get; init; }
}

/// <summary>
/// Saved user intent on a job that could not run immediately because the
/// project was busy. Persisted as <c>pending-intent.json</c> in the job
/// folder. The auto-pickup loop reads and consumes this when it runs the
/// job, which turns the auto-pickup into a UserContinue with the saved
/// follow-up + mode instead of a fresh start.
/// </summary>
public record PendingIntent
{
    public int Version { get; init; } = 1;
    /// <summary>One of <see cref="ContinueModes"/>.</summary>
    public string Mode { get; init; } = ContinueModes.Continue;
    public string Prompt { get; init; } = "";
    public DateTime SavedAt { get; init; }
    /// <summary><c>project-busy</c> for now.</summary>
    public string SavedReason { get; init; } = "project-busy";
    /// <summary>Diagnostic only: which job was active when this was saved.</summary>
    public string? SavedAgainstActiveJobId { get; init; }
}

/// <summary>
/// String values accepted on <see cref="ContinueJobRequest.Mode"/>. Kept as
/// constants (not an enum) so the JSON wire format is the literal string,
/// which is friendlier for hand-written API calls and stable across enum
/// renames.
/// </summary>
public static class ContinueModes
{
    public const string Continue = "continue";
    public const string Steer    = "steer";
    public const string Extend   = "extend";
    public const string NewTask  = "newTask";

    public static readonly string[] All = [Continue, Steer, Extend, NewTask];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Continue;
        var v = value.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase)) return m;
        return Continue;
    }
}

/// <summary>Curated entry in a CLI's model catalog returned by <c>GET /api/cli/{type}/models</c>.</summary>
public record CliModelInfo
{
    /// <summary>Model identifier passed to <c>--model &lt;id&gt;</c>.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-friendly label shown in dropdowns. Defaults to <c>Id</c> when empty.</summary>
    public string Label { get; init; } = "";
    /// <summary>Premium-request multiplier (Copilot only; null elsewhere).</summary>
    public double? Multiplier { get; init; }
    /// <summary>Optional vendor / family grouping (anthropic, openai, google, …).</summary>
    public string? Vendor { get; init; }
    /// <summary>Marks the entry the CLI uses by default when <c>--model</c> is omitted.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Supported thinking / reasoning levels for this model. Empty means no selector.</summary>
    public List<string> ThinkingLevels { get; init; } = [];
    /// <summary>Default thinking / reasoning level for this model, or null when unsupported.</summary>
    public string? DefaultThinkingLevel { get; init; }
    /// <summary>Whether this model should be offered for new work.</summary>
    public bool Available { get; init; } = true;
    /// <summary>Known but no longer preferred or not found in live CLI discovery.</summary>
    public bool Deprecated { get; init; }
    /// <summary>Short note explaining why an entry is unavailable or metadata-light.</summary>
    public string? AvailabilityNote { get; init; }
}

public record CliModelCatalog
{
    public List<CliModelInfo> Models { get; init; } = [];
    /// <summary>How the catalog was obtained: <c>config</c>, <c>cli-pty</c>, <c>hardcoded</c>, …</summary>
    public string Source { get; init; } = "config";
    /// <summary>UTC timestamp of the most recent (re)build. Useful for cache diagnostics.</summary>
    public DateTime FetchedAt { get; init; }
}

/// <summary>Canonical model id constants. Call sites should reference these instead of repeated literals.</summary>
public static class ModelIds
{
    public const string ClaudeOpus48 = "claude-opus-4-8";
    public const string ClaudeOpus47 = "claude-opus-4-7";
    public const string ClaudeOpus46 = "claude-opus-4-6";
    public const string ClaudeOpus45 = "claude-opus-4-5";
    public const string ClaudeSonnet46 = "claude-sonnet-4-6";
    public const string ClaudeSonnet45 = "claude-sonnet-4-5";
    public const string ClaudeHaiku45 = "claude-haiku-4-5";
    public const string Gpt5Codex = "gpt-5-codex";
    public const string Gpt41 = "gpt-4.1";
    public const string Gpt4o = "gpt-4o";
    public const string Gemini25Pro = "gemini-2.5-pro";
    public const string Gemini25Flash = "gemini-2.5-flash";
}

public sealed record ModelMetadata(
    string Id,
    string Label,
    string? Vendor,
    bool IsDefault,
    bool Deprecated,
    bool Available,
    decimal? InputPricePerMillion,
    decimal? OutputPricePerMillion,
    long? ContextWindow,
    string[]? Aliases = null,
    decimal? CacheReadPerMillionOverride = null,
    decimal? CacheWritePerMillionOverride = null);

/// <summary>
/// Single server-side source of truth for known model metadata: catalog labels,
/// defaults, pricing, context windows, aliases, and deprecation status.
/// </summary>
public static class ModelMetadataRegistry
{
    private static readonly ModelMetadata[] Entries =
    [
        Claude(ModelIds.ClaudeOpus48, "Claude Opus 4.8", isDefault: true, input: 5.00m, output: 25.00m, context: 200_000, aliases: ["claude-opus-4.8"]),
        Claude(ModelIds.ClaudeOpus47, "Claude Opus 4.7", input: 5.00m, output: 25.00m, context: 200_000, aliases: ["claude-opus-4.7"]),
        Claude(ModelIds.ClaudeOpus46, "Claude Opus 4.6", input: 5.00m, output: 25.00m, context: 200_000, aliases: ["claude-opus-4.6"]),
        Claude(ModelIds.ClaudeOpus45, "Claude Opus 4.5", input: 5.00m, output: 25.00m, context: 200_000, aliases: ["claude-opus-4.5"]),
        Claude(ModelIds.ClaudeSonnet46, "Claude Sonnet 4.6", input: 3.00m, output: 15.00m, context: 200_000, aliases: ["claude-sonnet-4.6"]),
        Claude(ModelIds.ClaudeSonnet45, "Claude Sonnet 4.5", input: 3.00m, output: 15.00m, context: 200_000, aliases: ["claude-sonnet-4.5"]),
        Claude(ModelIds.ClaudeHaiku45, "Claude Haiku 4.5", input: 1.00m, output: 5.00m, context: 200_000, aliases: ["claude-haiku-4.5"]),
        new(ModelIds.Gpt5Codex, "GPT-5 Codex", "openai", IsDefault: true, Deprecated: false, Available: true,
            InputPricePerMillion: 1.25m, OutputPricePerMillion: 10.00m, ContextWindow: 272_000,
            CacheReadPerMillionOverride: 0.125m, CacheWritePerMillionOverride: 1.25m),
        new(ModelIds.Gpt41, "GPT-4.1", "openai", IsDefault: false, Deprecated: false, Available: true,
            InputPricePerMillion: null, OutputPricePerMillion: null, ContextWindow: 1_000_000),
        new(ModelIds.Gpt4o, "GPT-4o", "openai", IsDefault: false, Deprecated: false, Available: true,
            InputPricePerMillion: null, OutputPricePerMillion: null, ContextWindow: 128_000),
        new(ModelIds.Gemini25Pro, "Gemini 2.5 Pro", "google", IsDefault: false, Deprecated: false, Available: true,
            InputPricePerMillion: null, OutputPricePerMillion: null, ContextWindow: 2_000_000),
        new(ModelIds.Gemini25Flash, "Gemini 2.5 Flash", "google", IsDefault: false, Deprecated: false, Available: true,
            InputPricePerMillion: null, OutputPricePerMillion: null, ContextWindow: 1_000_000),
    ];

    private static readonly IReadOnlyDictionary<string, ModelMetadata> ById = Entries
        .SelectMany(e => new[] { e.Id }.Concat(e.Aliases ?? []).Select(id => (id, e)))
        .ToDictionary(x => x.id, x => x.e, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModelMetadata> All => Entries;

    public static IReadOnlyList<ModelMetadata> ForVendor(string vendor)
        => Entries.Where(e => string.Equals(e.Vendor, vendor, StringComparison.OrdinalIgnoreCase)).ToList();

    public static ModelMetadata? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return ById.TryGetValue(id.Trim(), out var metadata) ? metadata : null;
    }

    public static string NormalizeId(string? id)
        => Find(id)?.Id ?? id?.Trim() ?? "";

    public static long? ContextWindowFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var metadata = Find(id);
        if (metadata?.ContextWindow is { } exact) return exact;

        foreach (var entry in Entries)
        {
            if (id.StartsWith(entry.Id, StringComparison.OrdinalIgnoreCase))
                return entry.ContextWindow;
        }
        return null;
    }

    public static CliModelInfo ToCliModelInfo(ModelMetadata metadata, string cliType, bool? isDefault = null)
        => new()
        {
            Id = metadata.Id,
            Label = metadata.Label,
            Vendor = metadata.Vendor,
            IsDefault = isDefault ?? metadata.IsDefault,
            Available = metadata.Available && !metadata.Deprecated,
            Deprecated = metadata.Deprecated,
            ThinkingLevels = CliThinkingLevels.For(cliType, metadata.Id).ToList(),
            DefaultThinkingLevel = CliThinkingLevels.DefaultFor(cliType, metadata.Id)
        };

    public static CliModelInfo UnknownCliModel(string id, string? label, string? vendor, string cliType)
        => new()
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
            Vendor = vendor,
            IsDefault = false,
            Available = true,
            Deprecated = false,
            AvailabilityNote = "Discovered from CLI; missing registry metadata.",
            ThinkingLevels = CliThinkingLevels.For(cliType, id).ToList(),
            DefaultThinkingLevel = CliThinkingLevels.DefaultFor(cliType, id)
        };

    private static ModelMetadata Claude(
        string id,
        string label,
        bool isDefault = false,
        decimal input = 0,
        decimal output = 0,
        long context = 200_000,
        string[]? aliases = null)
        => new(id, label, "anthropic", isDefault, Deprecated: false, Available: true,
            InputPricePerMillion: input, OutputPricePerMillion: output, ContextWindow: context, Aliases: aliases);
}
