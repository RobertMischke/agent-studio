using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli.Adapters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for Google's Antigravity CLI (<c>agentapi</c>).
/// <list type="bullet">
///   <item>New conversation: <c>agentapi new-conversation [--model=flash|pro|flash_lite] "&lt;prompt&gt;"</c>.</item>
///   <item>Resume: <c>agentapi send-message &lt;conversation_id&gt; "&lt;prompt&gt;"</c>.</item>
/// </list>
/// </summary>
public sealed class AntigravityCliService : CliExecutionServiceBase
{
    private string? _cliPathOverride;

    public AntigravityCliService(ILogger<AntigravityCliService> logger, IConfiguration configuration)
        : base(logger, configuration) { }

    public override string CliType => CliTypes.Gemini;

    public override string GetCliPath()
        => _cliPathOverride
           ?? _configuration["AntigravityCli:Path"]
           ?? _configuration["GeminiCli:Path"]
           ?? "agentapi";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("Antigravity CLI path set to: {Path}", GetCliPath());
    }

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
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            WorkingDirectory = workingDirectory
        };

        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName))
        {
            psi.ArgumentList.Add("send-message");
            psi.ArgumentList.Add(sessionName);
        }
        else
        {
            psi.ArgumentList.Add("new-conversation");
            var mappedModel = MapModel(model);
            if (!string.IsNullOrEmpty(mappedModel))
            {
                psi.ArgumentList.Add($"--model={mappedModel}");
            }
        }

        psi.ArgumentList.Add(string.IsNullOrEmpty(prompt) ? " " : prompt);
        return psi;
    }

    private static string? MapModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var lower = model.ToLowerInvariant();
        if (lower.Contains("lite") || lower.Contains("flash-lite") || lower.Contains("flash_lite")) return "flash_lite";
        if (lower.Contains("pro")) return "pro";
        if (lower.Contains("flash")) return "flash";
        return "flash";
    }

    protected override string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => null;

    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
        return GeminiEventAdapter.Map(line.Text, jobKey);
    }

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
        _logger.LogInformation("Captured Antigravity session id {Id}", info.CapturedSessionId);
    }

    public string? GetCapturedSessionId(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;

    public override IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
    {
        if (raw.Stream != "stdout" || string.IsNullOrWhiteSpace(raw.Text) || raw.Text[0] != '{')
        {
            yield return raw;
            yield break;
        }

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(raw.Text); } catch { }
        if (doc == null) { yield return raw; yield break; }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { yield return raw; yield break; }

        var cid = TryFindConversationId(root);
        if (!string.IsNullOrEmpty(cid))
        {
            yield return raw with { Text = $"● Session init {cid} (gemini-3)".TrimEnd() };
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != null)
        {
            switch (type)
            {
                case "message":
                    var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (role != "user")
                    {
                        var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(content))
                        {
                            foreach (var line in SplitLines(content))
                                yield return raw with { Text = line };
                        }
                    }
                    yield break;
                case "tool_call":
                case "tool_use":
                    var name = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "Tool"
                             : root.TryGetProperty("name",      out var n)  ? n.GetString()  ?? "Tool"
                             : "Tool";
                    var args = root.TryGetProperty("parameters", out var p) ? p
                             : root.TryGetProperty("input",      out var i) ? i
                             : root.TryGetProperty("args",       out var a) ? a : default;
                    yield return raw with { Text = FormatToolUse(name, args) };
                    yield break;
                case "tool_result":
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (status != null && status != "success")
                        yield return raw with { Text = $"  tool_result: {status}" };
                    yield break;
                case "result":
                    var resStatus = root.TryGetProperty("status", out var rest) ? rest.GetString() : "result";
                    yield return raw with { Text = $"● Result {resStatus}" };
                    yield break;
            }
        }

        if (root.TryGetProperty("response", out var resp))
        {
            var text = resp.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            if (text == null && resp.TryGetProperty("content", out var cn)) text = cn.GetString();
            if (text != null)
            {
                foreach (var line in SplitLines(text))
                    yield return raw with { Text = line };
                yield break;
            }
        }

        yield return raw;
    }

    private static string? TryFindConversationId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    if (prop.Name == "conversationId" || prop.Name == "conversation_id")
                    {
                        return prop.Value.GetString();
                    }
                    if (prop.Name == "id" && (element.TryGetProperty("conversationMetadata", out _) || element.TryGetProperty("metadata", out _)))
                    {
                        return prop.Value.GetString();
                    }
                }
                var sub = TryFindConversationId(prop.Value);
                if (sub != null) return sub;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var subEl in element.EnumerateArray())
            {
                var sub = TryFindConversationId(subEl);
                if (sub != null) return sub;
            }
        }
        return null;
    }

    private static string FormatToolUse(string name, JsonElement input)
    {
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

    public override (bool Available, string? Version, string Path) TestCliPath(string? path = null)
    {
        var testPath = ResolveExecutable(path?.Trim() ?? GetCliPath());
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = testPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var rawOutput = proc.StandardOutput.ReadToEnd().Trim();
            var rawError = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            var isAvailable = proc.ExitCode == 0 
                || rawOutput.Contains("unknown command: --version") 
                || rawError.Contains("unknown command: --version") 
                || rawOutput.Contains("Usage: agentapi");
            return (isAvailable, "1.0.0", testPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Antigravity CLI not available at path '{Path}'", testPath);
            return (false, null, testPath);
        }
    }

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var models = new List<CliModelInfo>
        {
            new() { Id = "flash",      Label = "Gemini Flash (Default)", Vendor = "google", IsDefault = true },
            new() { Id = "pro",        Label = "Gemini Pro",             Vendor = "google" },
            new() { Id = "flash_lite", Label = "Gemini Flash-Lite",      Vendor = "google" }
        };
        return Task.FromResult(new CliModelCatalog
        {
            Models = models,
            Source = "hardcoded",
            FetchedAt = DateTime.UtcNow
        });
    }
}
