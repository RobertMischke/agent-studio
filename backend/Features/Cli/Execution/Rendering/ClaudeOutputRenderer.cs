using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Renders Anthropic <c>claude</c> <c>stream-json</c> NDJSON frames into the
/// marker-line vocabulary the frontend activity-log parser classifies. This is
/// a verbatim extraction of the switch that used to live inline in
/// <see cref="ClaudeCliService"/>.<c>TransformReadLine</c>; the existing
/// <c>ClaudeCliServiceTests</c> drive the service (which now delegates here),
/// so the output is pinned byte-for-byte.
/// </summary>
public sealed class ClaudeOutputRenderer : ICliOutputRenderer
{
    public IEnumerable<CliOutputLine> Render(CliOutputLine raw)
    {
        // Stderr (and non-JSON stdout) passes through unchanged.
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(raw.Text); }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeOutputRenderer: swallow; handled below"); /* swallow; handled below */ }

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
            case "system":
            {
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                yield return raw with { Text = $"● Session {subtype ?? "system"} {sessionId ?? ""}".TrimEnd() };
                yield break;
            }
            case "assistant":
            {
                if (!root.TryGetProperty("message", out var msg)) { yield return raw; yield break; }
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    yield return raw;
                    yield break;
                }
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType == "text")
                    {
                        var text = part.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                        // Multi-line model text: split so the parser groups it as
                        // continuation lines on the same MESSAGES group.
                        foreach (var line in CliMarkerFormat.SplitLines(text))
                            yield return raw with { Text = line };
                    }
                    else if (partType == "tool_use")
                    {
                        var name  = part.TryGetProperty("name",  out var n) ? n.GetString() ?? "Tool" : "Tool";
                        var input = part.TryGetProperty("input", out var i) ? i : default;
                        yield return raw with { Text = FormatToolUse(name, input) };
                    }
                    else if (partType == "thinking")
                    {
                        // Skip extended-thinking blocks in the visible buffer —
                        // they're noisy and not user-actionable. Could surface
                        // behind a debug flag later.
                    }
                    else
                    {
                        yield return raw with { Text = $"● {partType ?? "?"}" };
                    }
                }
                yield break;
            }
            case "user":
            {
                // Tool results — emit a short indented continuation so the
                // parser keeps it under the preceding tool_use group.
                if (!root.TryGetProperty("message", out var msg)) { yield return raw; yield break; }
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    yield return raw;
                    yield break;
                }
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType != "tool_result") continue;
                    var isError = part.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    var resultText = ExtractToolResultText(part);
                    var firstLine = CliMarkerFormat.SplitLines(resultText).FirstOrDefault() ?? "";
                    if (string.IsNullOrWhiteSpace(firstLine)) continue;
                    yield return raw with
                    {
                        Stream = isError ? "stderr" : raw.Stream,
                        Text = "  " + (firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine)
                    };
                }
                yield break;
            }
            case "result":
            {
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : "result";
                var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                var resultText = root.TryGetProperty("result", out var rs) ? rs.GetString() : null;
                if (!string.IsNullOrWhiteSpace(resultText))
                {
                    foreach (var line in CliMarkerFormat.SplitLines(resultText!))
                        yield return raw with { Text = line, Stream = isError ? "stderr" : raw.Stream };
                }
                else
                {
                    yield return raw with { Text = $"● Result ({subtype})", Stream = isError ? "stderr" : raw.Stream };
                }
                yield break;
            }
            case "rate_limit_event":
            {
                // Anthropic streams a rate-limit telemetry frame per turn. The
                // marker is split into two halves: a human-friendly prefix
                // (visible in the activity log) and a machine-parseable
                // bracketed key=value tail that OnOutputLine reads back into a
                // typed snapshot for the live header pill.
                //
                //   ● Rate limit · five-hour · allowed · reset in 109 min  [window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]
                var info = root.TryGetProperty("rate_limit_info", out var rli) && rli.ValueKind == JsonValueKind.Object
                    ? rli : default;
                var status        = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("status",         out var s)  ? s.GetString()  : null;
                var window        = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("rateLimitType",  out var rt) ? rt.GetString() : null;
                var resetsAt      = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("resetsAt",       out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : 0;
                var overageStatus = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("overageStatus",  out var os) ? os.GetString() : null;
                var usingOverage  = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("isUsingOverage", out var uo) && uo.ValueKind == JsonValueKind.True;
                var resetIn = resetsAt > 0
                    ? CliMarkerFormat.FormatRelative(DateTimeOffset.FromUnixTimeSeconds(resetsAt) - DateTimeOffset.UtcNow)
                    : null;
                var human = new List<string> { "● Rate limit" };
                if (!string.IsNullOrWhiteSpace(window)) human.Add(window!.Replace('_', '-'));
                if (!string.IsNullOrWhiteSpace(status)) human.Add(status!);
                if (resetIn != null) human.Add($"reset in {resetIn}");
                var humanText = string.Join(" · ", human);
                var machineText = $"[window={window ?? "?"} status={status ?? "?"} resetsAt={resetsAt} overage={overageStatus ?? "-"} usingOverage={(usingOverage ? "true" : "false")}]";
                yield return raw with { Text = humanText + "  " + machineText };
                yield break;
            }
            default:
                // Catch-all: surface the frame type as a marker. Never leak raw
                // JSON into the activity log — that would also break our marker
                // classifier downstream. New Claude frame types should still
                // get an explicit case above when they carry useful info.
                var fallbackType = type ?? "frame";
                yield return raw with { Text = $"● {fallbackType}" };
                yield break;
        }
    }

    private static string FormatToolUse(string name, JsonElement input)
    {
        // Map Claude tool names → the marker-line vocabulary the existing
        // frontend parser classifies (Read/Search/Edit/Run/Todo/Task).
        string Get(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v)
                ? v.ToString() : "";

        return name switch
        {
            "Read"        => $"● Read {Get("file_path")}".TrimEnd(),
            "Write"       => $"● Write {Get("file_path")}".TrimEnd(),
            "Edit"        => $"● Edit {Get("file_path")}".TrimEnd(),
            "Glob"        => $"● Search glob {Get("pattern")}".TrimEnd(),
            "Grep"        => $"● Search {Get("pattern")}".TrimEnd(),
            "Bash"        => $"● Run {CliMarkerFormat.TrimSingleLine(Get("command"))}".TrimEnd(),
            "TodoWrite"   => "● Todo update",
            "Task"        => $"● Task {Get("description")}".TrimEnd(),
            "WebFetch"    => $"● Fetch {Get("url")}".TrimEnd(),
            "WebSearch"   => $"● Search web {Get("query")}".TrimEnd(),
            "NotebookEdit"=> $"● Edit notebook {Get("notebook_path")}".TrimEnd(),
            _             => $"● {name}"
        };
    }

    private static string ExtractToolResultText(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var c)) return "";
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array  => string.Join("\n",
                c.EnumerateArray()
                 .Where(e => e.ValueKind == JsonValueKind.Object
                          && e.TryGetProperty("type", out var et) && et.GetString() == "text")
                 .Select(e => e.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "")),
            _ => c.ToString()
        };
    }
}
