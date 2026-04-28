using System.Diagnostics;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for Anthropic's <c>claude</c> CLI.
/// <list type="bullet">
///   <item>First run: <c>claude -p "prompt" --name "session-name"</c> creates a named session.</item>
///   <item>Resume:    <c>claude -r "session-name" -p "prompt"</c>.</item>
///   <item>Sessions live in <c>~/.claude/projects/&lt;cwd&gt;/&lt;uuid&gt;.jsonl</c>.</item>
/// </list>
/// </summary>
public sealed class ClaudeCliService : CliExecutionServiceBase
{
    private string? _cliPathOverride;

    public ClaudeCliService(ILogger<ClaudeCliService> logger, IConfiguration configuration)
        : base(logger, configuration) { }

    public override string CliType => CliTypes.Claude;

    public override string GetCliPath()
        => _cliPathOverride
           ?? _configuration["ClaudeCli:Path"]
           ?? "claude";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("Claude CLI path set to: {Path}", GetCliPath());
    }

    protected override ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model)
    {
        // claude -p <prompt> [--name <s>] [-r <s>] [--model <m>]
        //   --output-format stream-json --verbose --dangerously-skip-permissions
        //
        // stream-json emits one NDJSON frame per assistant chunk / tool call /
        // tool result, flushed immediately. With the default text format the
        // CLI buffers its entire reply until the model finishes — that's why
        // the Activity Log used to stay empty for the whole run. `--verbose`
        // is required by the CLI when stream-json is combined with `-p`.
        // TransformReadLine() in this class normalises the frames into the
        // marker-line convention the frontend parser already understands.
        var args = new List<string> { "-p", Quote(prompt) };

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            if (resumeSession) { args.Add("-r"); args.Add(Quote(sessionName)); }
            else                { args.Add("--name"); args.Add(Quote(sessionName)); }
        }

        var normalizedModel = NormalizeModelId(model);
        if (!string.IsNullOrWhiteSpace(normalizedModel))
        {
            args.Add("--model"); args.Add(Quote(normalizedModel));
        }

        args.Add("--output-format"); args.Add("stream-json");
        args.Add("--verbose");
        args.Add("--dangerously-skip-permissions");

        return new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            Arguments = string.Join(' ', args),
            WorkingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Translates a single stream-json NDJSON frame from the Claude CLI into
    /// one or more human-readable marker lines, e.g.
    /// <c>● Read /path/to/file</c> or <c>● Edit src/foo.ts</c>. The format
    /// matches what the frontend's <c>activity-log.parser</c> already knows
    /// how to classify, so no per-CLI parser is needed.
    /// </summary>
    public override IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
    {
        // Stderr (and non-JSON stdout) passes through unchanged.
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        System.Text.Json.JsonDocument? doc = null;
        try { doc = System.Text.Json.JsonDocument.Parse(raw.Text); }
        catch { /* swallow; handled below */ }

        if (doc == null)
        {
            // Not JSON after all — surface raw.
            yield return raw;
            yield break;
        }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
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
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Array)
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
                        foreach (var line in SplitLines(text))
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
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    yield return raw;
                    yield break;
                }
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType != "tool_result") continue;
                    var isError = part.TryGetProperty("is_error", out var ie) && ie.ValueKind == System.Text.Json.JsonValueKind.True;
                    var resultText = ExtractToolResultText(part);
                    var firstLine = SplitLines(resultText).FirstOrDefault() ?? "";
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
                var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == System.Text.Json.JsonValueKind.True;
                var resultText = root.TryGetProperty("result", out var rs) ? rs.GetString() : null;
                if (!string.IsNullOrWhiteSpace(resultText))
                {
                    foreach (var line in SplitLines(resultText!))
                        yield return raw with { Text = line, Stream = isError ? "stderr" : raw.Stream };
                }
                else
                {
                    yield return raw with { Text = $"● Result ({subtype})", Stream = isError ? "stderr" : raw.Stream };
                }
                yield break;
            }
            default:
                yield return raw;
                yield break;
        }
    }

    private static string FormatToolUse(string name, System.Text.Json.JsonElement input)
    {
        // Map Claude tool names → the marker-line vocabulary the existing
        // frontend parser classifies (Read/Search/Edit/Run/Todo/Task).
        string Get(string key) =>
            input.ValueKind == System.Text.Json.JsonValueKind.Object && input.TryGetProperty(key, out var v)
                ? v.ToString() : "";

        return name switch
        {
            "Read"        => $"● Read {Get("file_path")}".TrimEnd(),
            "Write"       => $"● Write {Get("file_path")}".TrimEnd(),
            "Edit"        => $"● Edit {Get("file_path")}".TrimEnd(),
            "Glob"        => $"● Search glob {Get("pattern")}".TrimEnd(),
            "Grep"        => $"● Search {Get("pattern")}".TrimEnd(),
            "Bash"        => $"● Run {TrimSingleLine(Get("command"))}".TrimEnd(),
            "TodoWrite"   => "● Todo update",
            "Task"        => $"● Task {Get("description")}".TrimEnd(),
            "WebFetch"    => $"● Fetch {Get("url")}".TrimEnd(),
            "WebSearch"   => $"● Search web {Get("query")}".TrimEnd(),
            "NotebookEdit"=> $"● Edit notebook {Get("notebook_path")}".TrimEnd(),
            _             => $"● {name}"
        };
    }

    private static string ExtractToolResultText(System.Text.Json.JsonElement part)
    {
        if (!part.TryGetProperty("content", out var c)) return "";
        return c.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => c.GetString() ?? "",
            System.Text.Json.JsonValueKind.Array  => string.Join("\n",
                c.EnumerateArray()
                 .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.Object
                          && e.TryGetProperty("type", out var et) && et.GetString() == "text")
                 .Select(e => e.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "")),
            _ => c.ToString()
        };
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line;
    }

    private static string TrimSingleLine(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim() is { } t && t.Length > 200 ? t[..200] + "…" : s.Trim();

    // Claude's `-r` flag expects a session UUID written by the CLI itself.
    // Slug-style names from another CLI (e.g. Copilot's "taskboard-...") cause
    // the process to hang instead of erroring out, so reject anything that
    // isn't a 36-char canonical UUID.
    private static readonly System.Text.RegularExpressions.Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public override bool IsCompatibleSessionName(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName) && UuidRegex.IsMatch(sessionName);

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        // No live discovery yet — surface the well-known Claude 4.x family. The
        // user picks one, the CLI validates. Empty list also works (default).
        var models = new List<CliModelInfo>
        {
            new() { Id = "claude-opus-4-7",       Label = "Claude Opus 4.7",     Vendor = "anthropic" },
            new() { Id = "claude-sonnet-4-6",     Label = "Claude Sonnet 4.6",   Vendor = "anthropic", IsDefault = true },
            new() { Id = "claude-haiku-4-5",      Label = "Claude Haiku 4.5",    Vendor = "anthropic" }
        };
        return Task.FromResult(new CliModelCatalog
        {
            Models = models,
            Source = "hardcoded",
            FetchedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Coerces the dotted model-version forms users tend to type or paste
    /// (<c>claude-opus-4.7</c>, <c>claude-sonnet-4.6</c>) into the dashed form
    /// the Anthropic CLI requires (<c>claude-opus-4-7</c>). Any other model
    /// string is returned unchanged so non-standard ids still flow through.
    /// </summary>
    public static string? NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;
        var trimmed = model.Trim();
        if (!trimmed.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return trimmed;
        // Replace dots between digits ("4.7" → "4-7") without touching dots in
        // unrelated positions (none exist in real Claude ids today, but be safe).
        return System.Text.RegularExpressions.Regex.Replace(trimmed, @"(?<=\d)\.(?=\d)", "-");
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
