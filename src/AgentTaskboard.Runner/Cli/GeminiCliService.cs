using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli.Adapters;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for Google's <c>gemini</c> CLI (npm package <c>@google/gemini-cli</c>).
/// <list type="bullet">
///   <item>Headless run: <c>gemini -p "&lt;prompt&gt;" -o stream-json --skip-trust -y [-m &lt;model&gt;]</c>.</item>
///   <item>Resume: <c>gemini -r &lt;uuid|index|latest&gt; -p "&lt;prompt&gt;" ...</c>.</item>
///   <item>Sessions live in <c>~/.gemini/tmp/&lt;project-slug&gt;/chats/session-*.json</c>; the
///         project slug map is in <c>~/.gemini/projects.json</c>.</item>
///   <item>The CLI emits the session UUID on the first <c>{"type":"init",...}</c> stream-json
///         frame, captured here for later resume.</item>
/// </list>
/// </summary>
public sealed class GeminiCliService : CliExecutionServiceBase
{
    private string? _cliPathOverride;

    public GeminiCliService(ILogger<GeminiCliService> logger, IConfiguration configuration)
        : base(logger, configuration) { }

    public override string CliType => CliTypes.Gemini;

    public override string GetCliPath()
        => _cliPathOverride
           ?? _configuration["GeminiCli:Path"]
           ?? "gemini";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("Gemini CLI path set to: {Path}", GetCliPath());
    }

    // The CLI accepts --resume <uuid|index|latest>. We only persist UUIDs (captured
    // from the init frame) so cross-CLI session names are rejected — same pattern
    // as Claude/Codex.
    private static readonly Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    public override bool IsCompatibleSessionName(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName) && UuidRegex.IsMatch(sessionName);

    protected override ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel,
        string? permissionMode)
    {
        // gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <id>] [-r <uuid>]
        //
        // ADR-0014 default-deny stdin: the rendered prompt rides as the
        // -p value via ProcessStartInfo.ArgumentList, which escapes per
        // Win32 CommandLineToArgvW rules. The previous code used a
        // " " placeholder + stdin pipe to dodge cmd.exe argv quoting,
        // but that introduced the same pipe-inheritance race
        // claude-code#771 documents and the OSS-orchestration survey
        // sees across every CLI. Argv-via-ArgumentList preserves
        // multi-line content verbatim; Windows' command-line cap is
        // 32767 chars and our rendered prompts are well under that.
        //
        // -y / --yolo: auto-approve tool calls (analogous to Claude's
        //              --dangerously-skip-permissions). Required for
        //              unattended runs because the default tool-approval
        //              prompt is interactive.
        // --skip-trust: bypass the "Do you trust this folder?" dialog.
        //               Without it the CLI blocks on a modal dialog and
        //               never reaches the prompt.
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(string.IsNullOrEmpty(prompt) ? " " : prompt);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("stream-json");

        // Permission posture is resolved per-project. Default YOLO renders the
        // historic "--skip-trust -y" pair; every mode keeps --skip-trust so the
        // folder-trust modal can never hang an unattended run. A null mode
        // normalizes to YOLO. See CliPermissionFlags / the sandbox-and-yolo doc.
        foreach (var flag in CliPermissionFlags.For(CliType, permissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);
        }

        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName))
        {
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(sessionName);
        }

        return psi;
    }

    /// <summary>
    /// ADR-0014: Gemini receives the prompt as the -p value via argv
    /// (see <see cref="BuildStartInfo"/>); returning null tells the
    /// base class not to redirect stdin and prevents the pipe-
    /// inheritance race that motivated the ADR.
    /// </summary>
    protected override string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => null;

    /// <summary>
    /// Bridge to <see cref="GeminiEventAdapter"/>. Each raw stdout line
    /// is passed through and emitted on
    /// <see cref="CliExecutionServiceBase.OnRunEvent"/>.
    /// </summary>
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
        return GeminiEventAdapter.Map(line.Text, jobKey);
    }

    // The init frame's UUID is rendered by TransformReadLine into a marker line
    //   "● Session init <uuid> (<model>)"
    // and we capture from that. The base class invokes OnOutputLine on the
    // transformed line, so reading the raw JSON here is not possible.
    private static readonly Regex SessionInitRegex = new(
        @"●\s*Session init\s+(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    protected override void OnOutputLine(ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;
        if (line.Text == null) return;
        var m = SessionInitRegex.Match(line.Text);
        if (!m.Success) return;

        info.CapturedSessionId = m.Groups["uuid"].Value;
        info.SessionName ??= info.CapturedSessionId;
        _logger.LogInformation("Captured Gemini session id {Id}", info.CapturedSessionId);
    }

    public string? GetCapturedSessionId(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;

    /// <summary>
    /// Translates a single stream-json NDJSON frame from the Gemini CLI into one or
    /// more marker lines that match the frontend activity-log parser's vocabulary.
    /// </summary>
    public override IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
    {
        // Pass-through stderr (Gemini prints "Warning: True color..." and "YOLO mode is enabled" on stderr)
        // and any non-JSON stdout (rare, but possible during startup).
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(raw.Text); } catch { /* handled below */ }
        if (doc == null) { yield return raw; yield break; }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { yield return raw; yield break; }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "init":
            {
                var sid   = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                var model = root.TryGetProperty("model",      out var m) ? m.GetString() : null;
                yield return raw with { Text = $"● Session init {sid} ({model})".TrimEnd() };
                yield break;
            }
            case "message":
            {
                var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role == "user") { yield break; } // echo of our own prompt — no value to log
                var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(content)) yield break;
                foreach (var line in SplitLines(content))
                    yield return raw with { Text = line };
                yield break;
            }
            case "tool_call":
            case "tool_use":
            {
                // Real frame shape (gemini-cli 0.39.x):
                //   {"type":"tool_use","tool_name":"run_shell_command","tool_id":"...",
                //    "parameters":{"command":"echo hello","description":"..."}}
                var name = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "Tool"
                         : root.TryGetProperty("name",      out var n)  ? n.GetString()  ?? "Tool"
                         : "Tool";
                var args = root.TryGetProperty("parameters", out var p) ? p
                         : root.TryGetProperty("input",      out var i) ? i
                         : root.TryGetProperty("args",       out var a) ? a : default;
                yield return raw with { Text = FormatToolUse(name, args) };
                yield break;
            }
            case "tool_result":
            {
                // Success frames carry only {tool_id, status} — no payload to surface.
                // Errors come through stderr (Gemini prints "Error executing tool ..."
                // on stderr) so we don't duplicate the message here.
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (status != null && status != "success")
                    yield return raw with { Text = $"  tool_result: {status}" };
                yield break;
            }
            case "result":
            {
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "result";
                var isError = status == "error" || status == "failed";
                if (root.TryGetProperty("stats", out var stats)
                    && stats.TryGetProperty("duration_ms", out var dur)
                    && stats.TryGetProperty("total_tokens", out var tot))
                {
                    yield return raw with
                    {
                        Text = $"● Result {status} ({tot.GetInt64()} tokens, {dur.GetInt64()}ms)",
                        Stream = isError ? "stderr" : raw.Stream
                    };
                }
                else
                {
                    yield return raw with { Text = $"● Result {status}", Stream = isError ? "stderr" : raw.Stream };
                }
                yield break;
            }
            default:
                yield return raw;
                yield break;
        }
    }

    private static string FormatToolUse(string name, JsonElement input)
    {
        // Map the well-known Gemini built-in tool names to the same marker-line
        // vocabulary Claude uses, so the existing frontend parser classifies them.
        // Names verified against @google/gemini-cli's ToolRegistry built-ins.
        string Get(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) ? v.ToString() : "";

        return name switch
        {
            "read_file"     or "ReadFile"     => $"● Read {Get("absolute_path")}{Get("path")}".TrimEnd(),
            "write_file"    or "WriteFile"    => $"● Write {Get("absolute_path")}{Get("path")}".TrimEnd(),
            "edit"          or "Edit"
                            or "replace"      => $"● Edit {Get("file_path")}{Get("path")}".TrimEnd(),
            "glob"          or "Glob"         => $"● Search glob {Get("pattern")}".TrimEnd(),
            "search_file_content"
                            or "Grep"         => $"● Search {Get("pattern")}".TrimEnd(),
            "run_shell_command"
                            or "Shell"
                            or "Bash"         => $"● Run {TrimSingleLine(Get("command"))}".TrimEnd(),
            "web_fetch"     or "WebFetch"     => $"● Fetch {Get("url")}".TrimEnd(),
            "google_web_search"
                            or "WebSearch"    => $"● Search web {Get("query")}".TrimEnd(),
            _                                  => $"● {name}"
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

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        // The CLI's -m flag accepts model ids verbatim (e.g. "gemini-2.5-pro").
        // The "auto" tier transparently picks per request and reports per-model usage in the
        // result frame. No live discovery endpoint is exposed; the bundle ships a static
        // model registry. Keep the list short and current.
        var models = new List<CliModelInfo>
        {
            new() { Id = "auto-gemini-3",         Label = "Auto (default)",         Vendor = "google", IsDefault = true },
            new() { Id = "gemini-2.5-pro",        Label = "Gemini 2.5 Pro",         Vendor = "google" },
            new() { Id = "gemini-2.5-flash",      Label = "Gemini 2.5 Flash",       Vendor = "google" },
            new() { Id = "gemini-2.5-flash-lite", Label = "Gemini 2.5 Flash-Lite",  Vendor = "google" },
            new() { Id = "gemini-3-flash-preview",Label = "Gemini 3 Flash Preview", Vendor = "google" }
        };
        return Task.FromResult(new CliModelCatalog
        {
            Models = models,
            Source = "hardcoded",
            FetchedAt = DateTime.UtcNow
        });
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
