using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli.Adapters;
using OrchestratorApi.Services.Pty;

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
    private readonly CodexModelDiscovery _modelDiscovery;
    private string? _cliPathOverride;

    public CodexCliService(
        ILogger<CodexCliService> logger,
        IConfiguration configuration,
        CodexModelDiscovery modelDiscovery)
        : base(logger, configuration)
    {
        _modelDiscovery = modelDiscovery;
    }

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
        // codex exec [resume <uuid>] [--json] [-m <model>] [PROMPT]
        //
        // ADR-0014 default-deny stdin: prompt is the LAST positional argv,
        // not piped via stdin. Codex's "-" arg ("read instructions from
        // stdin") is the alternative path; we use the positional path
        // because it removes the inherited-pipe-handle race that Anthropic
        // documented in claude-code#771 and that the OSS-orchestration
        // survey identified across all four CLIs. ProcessStartInfo
        // .ArgumentList lets .NET escape per Win32 CommandLineToArgvW
        // rules, which preserves multi-line / quoted prompt content
        // verbatim - Windows' command-line cap is 32767 chars; rendered
        // prompts are well under that.
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add("exec");

        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName))
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(sessionName);
        }

        // --json keeps stdout machine-readable so we can extract the session_meta UUID.
        psi.ArgumentList.Add("--json");

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);
        }

        if (!string.IsNullOrEmpty(prompt))
        {
            psi.ArgumentList.Add(prompt);
        }

        return psi;
    }

    /// <summary>
    /// ADR-0014: Codex receives the prompt as a positional argv (see
    /// <see cref="BuildStartInfo"/>). Returning null tells the base class
    /// not to redirect stdin, preventing the pipe-inheritance race that
    /// motivated ADR-0014.
    /// </summary>
    protected override string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => null;

    /// <summary>
    /// Bridge to <see cref="CodexEventAdapter"/>. Each raw stdout line is
    /// passed through and emitted on <see cref="CliExecutionServiceBase.OnRunEvent"/>.
    /// </summary>
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
        return CodexEventAdapter.Map(line.Text, jobKey);
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
        => _modelDiscovery.GetAsync(GetCliPath(), forceRefresh, ct);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
