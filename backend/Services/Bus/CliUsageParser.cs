using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Bus;

/// <summary>
/// Single parsed snapshot a CLI's "turn completed" frame yields. Source of truth
/// for token / context-window extraction; the bridge wraps this into
/// <see cref="AgentMessageTokens"/> + <see cref="AgentMessageContextWindow"/>.
/// </summary>
/// <remarks>
/// The legacy code parsed <c>usage</c> in two places (OrchestratorRunner.ParseResult
/// and ClaudeEventAdapter.FormatUsage). This record removes that duplication and
/// gives both call sites the same enrichment (context-window snapshot, file count,
/// reasoning-tokens roll-up where the model exposes it).
/// </remarks>
public sealed record ParsedTurnUsage(
    string? Model,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? ReasoningOutput,
    AgentMessageContextWindow? ContextWindow)
{
    /// <summary>Sum of all tokens that occupied the context this turn.</summary>
    public long ContextUsed => Input + CacheRead;

    public AgentMessageTokens ToBusTokens() => new(
        Input: Input,
        Output: Output,
        CacheRead: CacheRead == 0 ? null : CacheRead,
        CacheWrite: CacheWrite == 0 ? null : CacheWrite,
        Model: Model,
        Dollars: null,
        ContextWindow: ContextWindow);
}

/// <summary>
/// Maps a single CLI's "turn finished" JSON frame onto <see cref="ParsedTurnUsage"/>.
/// </summary>
public interface ICliUsageParser
{
    /// <summary>CLI identifier this parser handles (lowercase, e.g. "claude", "codex").</summary>
    string CliType { get; }

    /// <summary>
    /// Try to extract token + context-window data from one JSON object the CLI
    /// emits when a turn finishes. Returns false when the frame does not carry
    /// usage data (tool frames, session-init frames, etc.). Never throws.
    /// </summary>
    /// <param name="frame">Parsed JSON object the CLI wrote (one NDJSON line, or
    /// the full <c>--print --output-format=json</c> blob).</param>
    /// <param name="modelHint">Fallback model id if the frame does not echo one.</param>
    /// <param name="modelRegistry">Lookup for the model's context-window
    /// total. May return null for unknown models; the parser then leaves
    /// <see cref="AgentMessageContextWindow.TotalSize"/> unset.</param>
    /// <param name="usage">The parsed snapshot when the return is true.</param>
    bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage);
}

/// <summary>
/// Resolves a model id to its known context-window size. Small, in-memory; the
/// list is static at process start. Adding a model is a one-line edit; we keep
/// it local so a Claude/Codex release does not break parsing on a model
/// rename - the parser just leaves the field unset and the timeline still
/// shows tokens.
/// </summary>
public interface ICliModelRegistry
{
    /// <summary>Total context-window size in tokens, or null when unknown.</summary>
    long? TotalContextSize(string? modelId);
}

/// <summary>Static, in-memory registry. See class docs for entries.</summary>
public sealed class CliModelRegistry : ICliModelRegistry
{
    // Conservative defaults pulled from public model cards as of 2026-05.
    // Adding a model: one line. Removing one: safe - parser leaves the field unset.
    private static readonly Dictionary<string, long> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Claude family
        ["claude-opus-4-7"]     = 200_000,
        ["claude-opus-4-6"]     = 200_000,
        ["claude-sonnet-4-6"]   = 200_000,
        ["claude-sonnet-4-5"]   = 200_000,
        ["claude-haiku-4-5"]    = 200_000,
        // Codex / OpenAI family
        ["gpt-5-codex"]         = 272_000,
        ["gpt-4.1"]             = 1_000_000,
        ["gpt-4o"]              =   128_000,
        // Gemini
        ["gemini-2.5-pro"]      = 2_000_000,
        ["gemini-2.5-flash"]    = 1_000_000,
    };

    public long? TotalContextSize(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        if (Sizes.TryGetValue(modelId, out var v)) return v;

        // Prefix match for forward compat: "claude-sonnet-4-6-20260301" -> 200k.
        foreach (var (key, val) in Sizes)
        {
            if (modelId.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return val;
        }
        return null;
    }
}

/// <summary>
/// Claude Code's usage extractor. Handles both the <c>--print
/// --output-format=json</c> blob (orchestrator path) and the
/// <c>result</c> frame from <c>stream-json</c> (task agent path) - they share
/// the same <c>usage</c> shape.
/// </summary>
public sealed class ClaudeUsageParser : ICliUsageParser
{
    public string CliType => "claude";

    public bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage)
    {
        usage = null!;
        if (frame.ValueKind != JsonValueKind.Object) return false;

        // Both flows have a top-level "usage" object on a "result"-shaped frame.
        if (!frame.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;

        var declaredModel = frame.TryGetProperty("model", out var md) ? md.GetString() : null;
        var model = declaredModel ?? modelHint;

        var input      = GetLong(u, "input_tokens");
        var output     = GetLong(u, "output_tokens");
        var cacheRead  = GetLong(u, "cache_read_input_tokens");
        var cacheWrite = GetLong(u, "cache_creation_input_tokens");

        var contextWindow = BuildContextWindow(model, input, cacheRead, modelRegistry);

        usage = new ParsedTurnUsage(
            Model: model,
            Input: input,
            Output: output,
            CacheRead: cacheRead,
            CacheWrite: cacheWrite,
            ReasoningOutput: null,
            ContextWindow: contextWindow);
        return true;
    }

    private static AgentMessageContextWindow? BuildContextWindow(string? model, long input, long cacheRead, ICliModelRegistry registry)
    {
        var total = registry.TotalContextSize(model);
        var used = input + cacheRead;
        if (total is null && used == 0) return null;
        return new AgentMessageContextWindow(
            TotalSize: total,
            Used: used,
            Remaining: total is { } t ? Math.Max(0, t - used) : null,
            // System-prompt + conversation split needs the cache_creation events
            // across the run; not derivable from a single frame. Left null until
            // the runner aggregates that across turns.
            SystemPromptTokens: null,
            ConversationTokens: null);
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;
}

/// <summary>
/// Codex's usage extractor for <c>turn.completed</c> frames in
/// <c>codex exec --json</c> output. Codex separates cached input tokens and
/// reasoning output tokens, both of which we surface.
/// </summary>
public sealed class CodexUsageParser : ICliUsageParser
{
    public string CliType => "codex";

    public bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage)
    {
        usage = null!;
        if (frame.ValueKind != JsonValueKind.Object) return false;

        // turn.completed wraps usage under "usage"; legacy session_meta does not.
        var type = frame.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!string.Equals(type, "turn.completed", StringComparison.Ordinal)) return false;

        if (!frame.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;

        var declaredModel = frame.TryGetProperty("model", out var md) ? md.GetString() : null;
        var model = declaredModel ?? modelHint;

        var input     = GetLong(u, "input_tokens");
        var cached    = GetLong(u, "cached_input_tokens");
        var output    = GetLong(u, "output_tokens");
        var reasoning = GetLong(u, "reasoning_output_tokens");

        var contextWindow = BuildContextWindow(model, input, cached, modelRegistry);

        usage = new ParsedTurnUsage(
            Model: model,
            Input: input,
            Output: output,
            CacheRead: cached,
            CacheWrite: 0,
            ReasoningOutput: reasoning,
            ContextWindow: contextWindow);
        return true;
    }

    private static AgentMessageContextWindow? BuildContextWindow(string? model, long input, long cached, ICliModelRegistry registry)
    {
        var total = registry.TotalContextSize(model);
        var used = input + cached;
        if (total is null && used == 0) return null;
        return new AgentMessageContextWindow(
            TotalSize: total,
            Used: used,
            Remaining: total is { } t ? Math.Max(0, t - used) : null);
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;
}

/// <summary>
/// Lookup that dispatches a CLI type to the matching parser. Single instance per
/// process; parsers are stateless so the registry is safe to share.
/// </summary>
public sealed class CliUsageParserRegistry
{
    private readonly Dictionary<string, ICliUsageParser> _byCli;

    public CliUsageParserRegistry(IEnumerable<ICliUsageParser> parsers)
    {
        _byCli = parsers.ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
    }

    public ICliUsageParser? Get(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        return _byCli.TryGetValue(cliType, out var p) ? p : null;
    }
}
