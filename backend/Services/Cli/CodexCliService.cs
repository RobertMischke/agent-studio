using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for the OpenAI <c>codex</c> CLI.
/// <list type="bullet">
///   <item>First run: <c>codex exec "prompt"</c> auto-creates a session UUID.</item>
///   <item>Resume:    <c>codex exec resume &lt;uuid&gt; "prompt"</c>.</item>
///   <item>The session UUID is captured from the first <c>session_meta</c> JSON line on stdout.</item>
/// </list>
/// </summary>
public sealed class CodexCliService : CliExecutionServiceBase
{
    private string? _cliPathOverride;

    public CodexCliService(ILogger<CodexCliService> logger, IConfiguration configuration)
        : base(logger, configuration) { }

    public override string CliType => CliTypes.Codex;

    // Codex resumes by UUID captured from session_meta. A slug from Copilot or
    // any other CLI is invalid and would make `codex exec resume` error out.
    private static readonly System.Text.RegularExpressions.Regex CodexUuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public override bool IsCompatibleSessionName(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName) && CodexUuidRegex.IsMatch(sessionName);

    public override string GetCliPath()
        => _cliPathOverride
           ?? _configuration["CodexCli:Path"]
           ?? "codex";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("Codex CLI path set to: {Path}", GetCliPath());
    }

    protected override ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model)
    {
        // For Codex, sessionName is the session UUID (or null for a fresh session).
        // codex exec [resume <uuid>] [--json] [-m <model>] "<prompt>"
        var args = new List<string> { "exec" };

        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName))
        {
            args.Add("resume");
            args.Add(Quote(sessionName));
        }

        // --json keeps stdout machine-readable so we can extract the session_meta UUID.
        args.Add("--json");

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(Quote(model));
        }

        args.Add(Quote(prompt));

        return new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            Arguments = string.Join(' ', args),
            WorkingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Codex emits the session UUID on the very first <c>{"type":"session_meta",...}</c>
    /// line of <c>--json</c> output. Capture it so the UI can persist the id and resume later.
    /// </summary>
    protected override void OnOutputLine(ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;
        if (line.Stream != "stdout") return;
        var text = line.Text?.TrimStart();
        if (text == null || !text.StartsWith('{')) return;
        if (!text.Contains("session_meta", StringComparison.Ordinal)) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                info.CapturedSessionId = id.GetString();
                info.SessionName ??= info.CapturedSessionId;
                _logger.LogInformation("Captured Codex session id {Id}", info.CapturedSessionId);
            }
        }
        catch { /* not json — ignore */ }
    }

    /// <summary>
    /// After a Codex run finishes, surface the captured session UUID so callers can
    /// persist it (the base class doesn't know about session ids).
    /// </summary>
    public string? GetCapturedSessionId(string jobKey)
    {
        return _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;
    }

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var models = new List<CliModelInfo>
        {
            new() { Id = "gpt-5",          Label = "GPT-5",          Vendor = "openai", IsDefault = true },
            new() { Id = "gpt-5-mini",     Label = "GPT-5 Mini",     Vendor = "openai" },
            new() { Id = "o4-mini",        Label = "o4-mini",        Vendor = "openai" }
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
