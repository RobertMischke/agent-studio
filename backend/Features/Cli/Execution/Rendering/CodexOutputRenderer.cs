using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Renders Codex <c>codex exec --json</c> JSONL frames into the SAME marker-line
/// vocabulary <see cref="ClaudeOutputRenderer"/> emits, so the frontend
/// activity-log <c>classifyAction</c> buckets a Codex run identically to a Claude
/// run. Before this existed Codex had no <c>TransformReadLine</c> override and raw
/// JSONL leaked into the Activity Log unfiltered.
///
/// <para>
/// <b>Deliberate marker equivalences (AC#3, frame-level, not byte-level).</b>
/// Codex's frame catalogue differs from Claude's, so the marker TEXT differs, but
/// each maps to a verb <c>classifyAction</c> already understands:
/// </para>
/// <list type="bullet">
///   <item><c>thread.started</c> / <c>session_meta</c> -&gt; <c>● Session {id}</c>
///   (Claude emits <c>● Session init {id}</c>; both classify as the session marker).</item>
///   <item><c>item.completed</c> agent_message -&gt; model text, multi-line split
///   (identical to Claude's assistant text path).</item>
///   <item>command items -&gt; <c>● Run {cmd}</c> (reuses Claude's Bash verb).</item>
///   <item>file_change -&gt; <c>● Edit {path}</c>; web_search -&gt; <c>● Search web {q}</c>;
///   update_plan -&gt; <c>● Todo update</c> (reuse Claude's verbs verbatim).</item>
///   <item><c>turn.completed</c> -&gt; <c>● Turn completed (tokens: N)</c>
///   (Codex analogue of Claude's <c>● Result (success)</c>).</item>
///   <item><c>turn.failed</c> -&gt; <c>● Turn failed: {reason}</c> on stderr.</item>
/// </list>
///
/// <para>
/// Pure and stateless like its typed-event twin
/// <see cref="Adapters.CodexEventAdapter"/> - no session-id capture here. Codex
/// session capture reads the RAW line in the driver's <c>MapLineToRunEvents</c>
/// hook, never the rendered marker, so the thread-id payload is not lost to the
/// <c>● Session</c> rewrite.
/// </para>
/// </summary>
public sealed class CodexOutputRenderer : ICliOutputRenderer
{
    private const string ToolRouterDiagnosticPrefix = "codex_core::tools::router: error=Exit code:";

    public IEnumerable<CliOutputLine> Render(CliOutputLine raw)
    {
        // codex-cli duplicates every non-zero command result on stderr as an
        // internal router diagnostic. The authoritative command_execution
        // item.completed frame follows with the command, captured output and
        // exit_code, so projecting this line creates a second, malformed tool
        // event ("expected: tool-result") between otherwise valid tool frames.
        // Suppress only the exact Codex router prefix; genuine stderr remains
        // visible and the completed item still renders as a failed Run marker.
        if (raw.Stream == "stderr" && IsToolRouterExitDiagnostic(raw.Text))
        {
            yield break;
        }

        // Other stderr (and non-JSON stdout) passes through unchanged.
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(raw.Text); }
        catch (Exception __ex) { SilentCatch.Note(__ex, "CodexOutputRenderer: swallow; handled below"); /* swallow; handled below */ }

        if (doc == null)
        {
            yield return raw;
            yield break;
        }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield return raw;
            yield break;
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "thread.started":
            {
                var id = root.TryGetProperty("thread_id", out var tid) ? tid.GetString() : null;
                yield return raw with { Text = $"● Session {id}".TrimEnd() };
                yield break;
            }
            case "session_meta":
            {
                yield return raw with { Text = $"● Session {SessionMetaId(root)}".TrimEnd() };
                yield break;
            }
            case "turn.started":
                // No visible marker; the first item frame carries the signal.
                yield break;
            case "turn.completed":
            {
                long tokens = 0;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    var input  = u.TryGetProperty("input_tokens",  out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                    var output = u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
                    tokens = input + output;
                    yield return raw with { Text = $"● Turn completed (tokens: {tokens})" };
                }
                else
                {
                    yield return raw with { Text = "● Turn completed" };
                }
                yield break;
            }
            case "turn.failed":
            {
                var reason = root.TryGetProperty("error", out var e)
                          && e.ValueKind == JsonValueKind.Object
                          && e.TryGetProperty("message", out var m)
                    ? (m.GetString() ?? "error")
                    : "error";
                yield return raw with { Text = $"● Turn failed: {CliMarkerFormat.TrimSingleLine(reason)}", Stream = "stderr" };
                yield break;
            }
            case "item.started":
                // Suppress: item.completed renders the same item; emitting both
                // would double every tool line in the Activity Log.
                yield break;
            case "item.completed":
            {
                foreach (var line in RenderItem(raw, root))
                    yield return line;
                yield break;
            }
            default:
                // Catch-all: surface the frame type as a marker. Never leak raw
                // JSON into the activity log - that would break the marker
                // classifier downstream. New Codex frame types should still get
                // an explicit case above when they carry useful info.
                yield return raw with { Text = $"● {type ?? "frame"}" };
                yield break;
        }
    }

    private static bool IsToolRouterExitDiagnostic(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith(ToolRouterDiagnosticPrefix, StringComparison.Ordinal)) return true;

        // Current codex-cli prefixes tracing diagnostics with an RFC3339
        // timestamp and level. Keep that wrapper part of the contract instead
        // of using Contains(), which could hide command-owned stderr that only
        // quotes the diagnostic text.
        var marker = trimmed.IndexOf(" ERROR ", StringComparison.Ordinal);
        return marker >= 0
            && trimmed.AsSpan(marker + " ERROR ".Length)
                .StartsWith(ToolRouterDiagnosticPrefix, StringComparison.Ordinal);
    }

    private static IEnumerable<CliOutputLine> RenderItem(CliOutputLine raw, JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            yield return raw with { Text = "● item" };
            yield break;
        }

        var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
        switch (itemType)
        {
            case "agent_message":
            {
                var text = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                // Multi-line model text: split so the parser groups it as
                // continuation lines on the same MESSAGES group (mirrors Claude).
                foreach (var line in CliMarkerFormat.SplitLines(text))
                    yield return raw with { Text = line };
                yield break;
            }
            case "reasoning":
                // Skip Codex reasoning blocks in the visible buffer - the
                // marker-line twin of Claude's suppressed "thinking" parts.
                yield break;
            case "command_execution":
            case "command_call":
            case "local_shell_call":
            {
                var cmd = ItemString(item, "command");
                var isError = item.TryGetProperty("exit_code", out var ec)
                           && ec.ValueKind == JsonValueKind.Number
                           && ec.TryGetInt64(out var code) && code != 0;
                yield return raw with
                {
                    Text = $"● Run {CliMarkerFormat.TrimSingleLine(cmd)}".TrimEnd(),
                    Stream = isError ? "stderr" : raw.Stream
                };
                yield break;
            }
            case "file_change":
            {
                yield return raw with { Text = $"● Edit {FileChangePath(item)}".TrimEnd() };
                yield break;
            }
            case "web_search":
            {
                yield return raw with { Text = $"● Search web {ItemString(item, "query")}".TrimEnd() };
                yield break;
            }
            case "update_plan":
            case "todo":
                yield return raw with { Text = "● Todo update" };
                yield break;
            case null:
                yield return raw with { Text = "● item" };
                yield break;
            default:
                yield return raw with { Text = $"● {itemType}" };
                yield break;
        }
    }

    /// <summary>
    /// Read a string-ish item field: a plain string, the first string in an
    /// array, or a number/other rendered via <c>ToString</c>. Mirrors the
    /// tolerance in <see cref="Adapters.CodexEventAdapter"/>.<c>ClassifyItem</c>.
    /// </summary>
    private static string ItemString(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Array  => v.EnumerateArray()
                                     .Where(e => e.ValueKind == JsonValueKind.String)
                                     .Select(e => e.GetString() ?? "")
                                     .FirstOrDefault() ?? "",
            JsonValueKind.Null   => "",
            _                    => v.ToString()
        };
    }

    /// <summary>
    /// A <c>file_change</c> item may carry the path under <c>path</c> or
    /// <c>file_path</c>; fall back to the first changed entry's path if present.
    /// </summary>
    private static string FileChangePath(JsonElement item)
    {
        foreach (var key in new[] { "path", "file_path" })
        {
            var s = ItemString(item, key);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        if (item.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var entry in ch.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    var s = ItemString(entry, "path");
                    if (!string.IsNullOrEmpty(s)) return s;
                }
        return "";
    }

    /// <summary>
    /// Legacy <c>session_meta</c> carries the id either at the root
    /// (<c>session_id</c>) or nested under <c>payload.id</c>.
    /// </summary>
    private static string? SessionMetaId(JsonElement root)
    {
        if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            return sid.GetString();
        if (root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String)
            return pid.GetString();
        return null;
    }
}
