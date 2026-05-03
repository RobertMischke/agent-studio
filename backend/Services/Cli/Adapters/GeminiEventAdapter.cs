using System.Text.Json;

namespace OrchestratorApi.Services.Cli.Adapters;

/// <summary>
/// Maps Gemini's <c>gemini -o stream-json</c> NDJSON frames onto the
/// typed <see cref="CliRunEvent"/> contract.
///
/// <para>
/// Frame catalogue (verified against gemini-cli on 2026-05-03):
/// </para>
/// <list type="bullet">
///   <item><c>init</c> with <c>session_id</c> -&gt; <see cref="CliRunEvent.SessionStarted"/>.</item>
///   <item><c>message</c> with <c>role=user</c> -&gt; ignored (echoes our prompt).</item>
///   <item><c>message</c> with <c>role=assistant</c> -&gt; <see cref="CliRunEvent.OutputDelta"/>(content).</item>
///   <item><c>tool_call</c> -&gt; <see cref="CliRunEvent.ToolStarted"/>.</item>
///   <item><c>tool_result</c> -&gt; <see cref="CliRunEvent.ToolCompleted"/>.</item>
///   <item><c>result</c> with <c>status=success</c> -&gt; <see cref="CliRunEvent.TurnCompleted"/>(usage).</item>
///   <item><c>result</c> with <c>status=error</c> / other -&gt; <see cref="CliRunEvent.TurnFailed"/>(reason).</item>
///   <item>everything else -&gt; <see cref="CliRunEvent.Unknown"/>.</item>
/// </list>
/// </summary>
public static class GeminiEventAdapter
{
    public static IEnumerable<CliRunEvent> Map(string jsonLine, string jobKey)
    {
        if (string.IsNullOrWhiteSpace(jsonLine) || jsonLine[0] != '{') yield break;

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { yield break; }

        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;

        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "init":
            {
                var id = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                yield return new CliRunEvent.SessionStarted(id) { JobKey = jobKey };
                yield break;
            }
            case "message":
            {
                var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (!string.Equals(role, "assistant", StringComparison.Ordinal)) yield break;
                var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (!string.IsNullOrEmpty(content))
                    yield return new CliRunEvent.OutputDelta(content) { JobKey = jobKey };
                yield break;
            }
            case "tool_call":
            {
                var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "tool") : "tool";
                string? arg = null;
                if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "command", "file_path", "path", "query", "url" })
                    {
                        if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            arg = v.GetString();
                            break;
                        }
                    }
                }
                yield return new CliRunEvent.ToolStarted(name, arg) { JobKey = jobKey };
                yield break;
            }
            case "tool_result":
            {
                var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "tool") : "tool";
                var isError = root.TryGetProperty("error", out var _err) && _err.ValueKind != JsonValueKind.Null;
                var firstLine = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String
                    ? FirstLine(o.GetString())
                    : null;
                yield return new CliRunEvent.ToolCompleted(name, isError, firstLine) { JobKey = jobKey };
                yield break;
            }
            case "result":
            {
                var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (string.Equals(status, "success", StringComparison.Ordinal))
                {
                    string? usage = null;
                    if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
                        usage = FormatStats(stats);
                    yield return new CliRunEvent.TurnCompleted(usage) { JobKey = jobKey };
                }
                else
                {
                    yield return new CliRunEvent.TurnFailed(status ?? "error") { JobKey = jobKey };
                }
                yield break;
            }
            default:
                yield return new CliRunEvent.Unknown(Truncate(jsonLine, 200)) { JobKey = jobKey };
                yield break;
        }
    }

    private static string FormatStats(JsonElement stats)
    {
        var input = stats.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
        var output = stats.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
        var cached = stats.TryGetProperty("cached", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
        var toolCalls = stats.TryGetProperty("tool_calls", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0;
        return $"input={input} output={output} cached={cached} tool_calls={toolCalls}";
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var i = s!.IndexOf('\n');
        return i >= 0 ? s[..i] : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
