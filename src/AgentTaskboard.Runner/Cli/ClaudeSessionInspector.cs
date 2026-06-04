using System.Globalization;
using System.Text.Json;

namespace OrchestratorApi.Services.Cli;

public record ClaudeSessionInfo(
    string SessionId,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    int TotalTokens,
    string? LastTurnAt,
    int TurnCount,
    string? Error);

/// <summary>
/// Cumulative token usage summed across every assistant turn in a Claude
/// session transcript. Unlike <see cref="ClaudeSessionInfo"/> (which carries
/// the latest turn's live snapshot), this aggregates the whole session so a
/// run's total spend can be reconstructed after the fact - including for
/// aborted / killed runs that never emit a terminal usage footer.
/// </summary>
public sealed record ClaudeSessionUsageAggregate(
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    int TurnCount,
    string? LastTurnAt);

/// <summary>
/// Reads telemetry directly from the Claude Code CLI's session JSONL file at
/// <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;uuid&gt;.jsonl</c>. The CLI
/// records every turn with <c>message.usage</c> on assistant frames, so we
/// can surface live tokens / model / context-cache state without spawning
/// an interactive PTY.
/// </summary>
public sealed class ClaudeSessionInspector
{
    private readonly ILogger<ClaudeSessionInspector> _logger;
    public ClaudeSessionInspector(ILogger<ClaudeSessionInspector> logger) { _logger = logger; }

    public ClaudeSessionInfo Inspect(string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new(sessionId, null, 0, 0, 0, 0, 0, null, 0, "No sessionId — start the job once first.");

        var jsonl = ResolveSessionFile(sessionId, workingDirectory);
        if (jsonl == null)
            return new(sessionId, null, 0, 0, 0, 0, 0, null, 0, $"Session log not found in ~/.claude/projects/ for cwd '{workingDirectory}'.");

        try
        {
            string? model = null;
            int input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
            string? lastTs = null;
            int turns = 0;

            // Open with FileShare.ReadWrite — the Claude CLI may still be writing
            // to this file while we read it. Reading a partial JSONL line is
            // tolerated (skipped by the parser).
            using var stream = new FileStream(jsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            foreach (var line in EnumerateLines(reader))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using var _ = doc;
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "assistant") continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;

                turns++;
                if (msg.TryGetProperty("model", out var m)) model = m.GetString() ?? model;
                if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    // The latest turn's usage replaces — input/output are per-turn,
                    // not cumulative. Cache numbers reflect the live cache state.
                    input         = u.TryGetProperty("input_tokens",                out var ip) && ip.ValueKind == JsonValueKind.Number ? ip.GetInt32() : input;
                    output        = u.TryGetProperty("output_tokens",               out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : output;
                    cacheRead     = u.TryGetProperty("cache_read_input_tokens",     out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : cacheRead;
                    cacheCreation = u.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : cacheCreation;
                }
                if (root.TryGetProperty("timestamp", out var ts)) lastTs = ts.GetString() ?? lastTs;
            }

            if (turns == 0)
                return new(sessionId, null, 0, 0, 0, 0, 0, null, 0, "Session file exists but has no assistant turns yet.");

            return new(sessionId, model, input, output, cacheRead, cacheCreation,
                       input + output + cacheRead + cacheCreation, lastTs, turns, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect Claude session {SessionId}", sessionId);
            return new(sessionId, null, 0, 0, 0, 0, 0, null, 0, ex.Message);
        }
    }

    /// <summary>
    /// Reconstructs the cumulative token usage for a session by summing the
    /// per-turn <c>message.usage</c> across every assistant frame in the
    /// transcript. Returns null when the session file cannot be located or
    /// read. Used for post-hoc token attribution (ASS-626 / ASS-665): the
    /// Claude CLI never reports a terminal usage footer, and a killed run
    /// loses even the final result frame, so the transcript is the only
    /// durable record of what the run actually spent.
    /// </summary>
    public ClaudeSessionUsageAggregate? AggregateUsage(string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var jsonl = ResolveSessionFile(sessionId, workingDirectory);
        if (jsonl == null) return null;

        try
        {
            using var stream = new FileStream(jsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return AggregateUsageFromLines(EnumerateLines(reader));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to aggregate Claude session {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Pure aggregation over transcript lines: sums input / output / cache-read
    /// / cache-creation tokens across all assistant turns that carry a usage
    /// block. Kept static + side-effect-free so the summation can be locked
    /// with fixture-based unit tests. Malformed JSON lines (including a
    /// partially-written trailing line) are skipped.
    /// </summary>
    public static ClaudeSessionUsageAggregate AggregateUsageFromLines(IEnumerable<string> lines)
    {
        string? model = null;
        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
        int turns = 0;
        string? lastTs = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using var _ = doc;
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "assistant") continue;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;

            turns++;
            if (msg.TryGetProperty("model", out var m)) model = m.GetString() ?? model;
            input         += ReadLong(u, "input_tokens");
            output        += ReadLong(u, "output_tokens");
            cacheRead     += ReadLong(u, "cache_read_input_tokens");
            cacheCreation += ReadLong(u, "cache_creation_input_tokens");
            if (root.TryGetProperty("timestamp", out var ts)) lastTs = ts.GetString() ?? lastTs;
        }

        return new ClaudeSessionUsageAggregate(
            model, input, output, cacheRead, cacheCreation,
            input + output + cacheRead + cacheCreation, turns, lastTs);
    }

    /// <summary>
    /// Renders an aggregate as the unstructured footer string the frontend's
    /// agent-usage block shows verbatim, e.g.
    /// <c>13.5M tokens (in 47.5k, out 128k, cache-read 13.4M, cache-write 12k)</c>.
    /// </summary>
    public static string FormatUsageString(ClaudeSessionUsageAggregate a)
    {
        var parts = new List<string>
        {
            $"in {Compact(a.InputTokens)}",
            $"out {Compact(a.OutputTokens)}"
        };
        if (a.CacheReadTokens > 0) parts.Add($"cache-read {Compact(a.CacheReadTokens)}");
        if (a.CacheCreationTokens > 0) parts.Add($"cache-write {Compact(a.CacheCreationTokens)}");
        return $"{Compact(a.TotalTokens)} tokens ({string.Join(", ", parts)})";
    }

    private static string Compact(long n)
    {
        if (n >= 1_000_000)
            return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000)
            return (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static long ReadLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static IEnumerable<string> EnumerateLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    private static string? ResolveSessionFile(string sessionId, string workingDirectory)
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) return null;
        var projects = Path.Combine(home, ".claude", "projects");
        if (!Directory.Exists(projects)) return null;

        // Claude encodes the cwd by replacing path separators / colons with `-`.
        // The drive-letter case isn't normalised, so we just match folders
        // case-insensitively against both the encoded form and the directory name.
        var encoded = Encode(workingDirectory);
        var candidates = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(projects))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, encoded, StringComparison.OrdinalIgnoreCase))
                candidates.Add(dir);
        }
        // Fall back: if no exact match, try any folder whose name's letters/digits
        // sequence matches — Claude has been known to tweak its encoding rules.
        if (candidates.Count == 0)
        {
            var simplified = Simplify(encoded);
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                if (Simplify(Path.GetFileName(dir)) == simplified) candidates.Add(dir);
            }
        }

        foreach (var dir in candidates)
        {
            var file = Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    private static string Encode(string path) => path.Replace('\\', '-').Replace(":", "-");
    private static string Simplify(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
