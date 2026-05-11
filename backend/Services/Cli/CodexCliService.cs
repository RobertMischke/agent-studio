using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli.Adapters;
using OrchestratorApi.Services.Pty;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for the OpenAI <c>codex</c> CLI.
/// <list type="bullet">
///   <item>First run: <c>codex exec "prompt"</c> auto-creates a session UUID.</item>
///   <item>Resume:    <c>codex exec resume &lt;uuid&gt; "prompt"</c>.</item>
///   <item>The session UUID is captured from the first <c>thread.started</c> JSON line
///         (codex-cli &gt;= 0.128) or the legacy <c>session_meta</c> frame.</item>
/// </list>
/// </summary>
public sealed class CodexCliService : CliExecutionServiceBase
{
    private readonly CodexModelDiscovery _modelDiscovery;
    private readonly CliUsageParserRegistry _usageParsers;
    private readonly ICliModelRegistry _modelRegistry;
    private string? _cliPathOverride;

    public CodexCliService(
        ILogger<CodexCliService> logger,
        IConfiguration configuration,
        CodexModelDiscovery modelDiscovery,
        CliUsageParserRegistry usageParsers,
        ICliModelRegistry modelRegistry)
        : base(logger, configuration)
    {
        _modelDiscovery = modelDiscovery;
        _usageParsers = usageParsers;
        _modelRegistry = modelRegistry;
    }

    public override string CliType => CliTypes.Codex;

    // Codex resumes by UUID captured from thread.started (or legacy session_meta).
    // A slug from Copilot or any other CLI is invalid and would make
    // `codex exec resume` error out.
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

        // --json keeps stdout machine-readable so we can extract the session UUID
        // from the first thread.started (or legacy session_meta) frame.
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
    /// <para>
    /// We also opportunistically parse <c>turn.completed</c> frames here so
    /// the captured <see cref="ParsedTurnUsage"/> lands on <c>ProcInfo</c>
    /// <b>before</b> the typed <c>TurnCompleted</c> event is raised. Order
    /// matters: <see cref="CliExecutionServiceBase"/> runs
    /// <see cref="MapLineToRunEvents"/> first, raises the typed events, and
    /// only then fires <see cref="OnOutputLine"/>. Doing the usage capture
    /// inside <c>OnOutputLine</c> (or anywhere downstream of the event raise)
    /// races the runner's subscriber, which immediately calls back into
    /// <see cref="GetLastParsedTurnUsage"/> to mirror the spend onto the bus.
    /// </para>
    /// </summary>
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();

        if (_processes.TryGetValue(jobKey, out var info))
        {
            TryCaptureTurnUsage(info, line);
        }

        return CodexEventAdapter.Map(line.Text, jobKey);
    }

    /// <summary>
    /// Codex emits the session UUID on the first <c>{"type":"thread.started",
    /// "thread_id":"&lt;uuid&gt;"}</c> line of <c>--json</c> output (codex-cli
    /// &gt;= 0.128). Older builds used <c>{"type":"session_meta","payload":{"id":"&lt;uuid&gt;"}}</c>
    /// which we still accept. Without this capture the per-job session store
    /// stays empty and every follow-up rebuilds context from disk via Recovery
    /// instead of <c>codex exec resume &lt;uuid&gt;</c>, throwing away Codex's
    /// own prompt-cache.
    /// </summary>
    protected override void OnOutputLine(ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;
        if (line.Stream != "stdout") return;

        var id = TryExtractSessionId(line.Text);
        if (id == null) return;

        info.CapturedSessionId = id;
        info.SessionName ??= id;
        _logger.LogInformation("Captured Codex session id {Id}", id);
    }

    /// <summary>
    /// Parse a <c>turn.completed</c> frame's <c>usage</c> block via the
    /// shared <see cref="CodexUsageParser"/> and stash the parsed snapshot on
    /// <see cref="CliExecutionServiceBase.ProcInfo.LastParsedUsage"/>. The
    /// runner consumes the stash when the matching <c>TurnCompleted</c> typed
    /// event arrives and mirrors it onto the agent message bus as
    /// <c>kind:token-usage</c>. Without this, the Codex coding-agent's own
    /// per-turn spend is invisible to <c>BusAggregationCache</c>, the project
    /// token summary, and the workspace quota strip. Best-effort: a malformed
    /// frame or parser miss leaves the previous snapshot untouched.
    /// </summary>
    private void TryCaptureTurnUsage(ProcInfo info, CliOutputLine line)
    {
        var text = line.Text?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return;
        // Fast prefilter: only attempt JSON parsing for frames we care about.
        if (!text.Contains("turn.completed", StringComparison.Ordinal)) return;

        var parser = _usageParsers.Get(CliTypes.Codex);
        if (parser == null) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var modelHint = info.Execution.Model;
            if (!parser.TryParse(doc.RootElement, modelHint, _modelRegistry, out var usage)) return;

            info.LastParsedUsage = usage;
            info.LastParsedUsageAt = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
        }
        catch (JsonException) { /* malformed frame; nothing to capture */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Codex turn-usage capture skipped"); }
    }

    /// <summary>
    /// Parses a single <c>codex exec --json</c> stdout line and returns the
    /// session UUID iff the line is a <c>thread.started</c> (preferred) or
    /// legacy <c>session_meta</c> frame carrying a canonical UUID. Returns
    /// <c>null</c> for every other line shape (other frame types, malformed
    /// JSON, non-JSON text, non-UUID ids). Exposed <c>internal</c> so the
    /// regression test for the codex-cli 0.128 capture path can drive it
    /// without spinning up a real CLI process.
    /// </summary>
    internal static string? TryExtractSessionId(string? line)
    {
        var text = line?.TrimStart();
        if (string.IsNullOrEmpty(text) || text[0] != '{') return null;

        // Fast prefilter: only attempt JSON parsing for frame types we care about.
        var hasThreadStarted = text.Contains("thread.started", StringComparison.Ordinal);
        var hasSessionMeta = text.Contains("session_meta", StringComparison.Ordinal);
        if (!hasThreadStarted && !hasSessionMeta) return null;

        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "thread.started", StringComparison.Ordinal)
                && root.TryGetProperty("thread_id", out var tid)
                && tid.ValueKind == JsonValueKind.String)
            {
                id = tid.GetString();
            }
            else if (string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                // Legacy: id may live at payload.id or at session_id on root.
                if (root.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("id", out var pid)
                    && pid.ValueKind == JsonValueKind.String)
                {
                    id = pid.GetString();
                }
                else if (root.TryGetProperty("session_id", out var sid)
                    && sid.ValueKind == JsonValueKind.String)
                {
                    id = sid.GetString();
                }
            }
        }
        catch { return null; }

        return !string.IsNullOrWhiteSpace(id) && CodexUuidRegex.IsMatch(id) ? id : null;
    }

    /// <summary>
    /// After a Codex run finishes, surface the captured session UUID so callers can
    /// persist it (the base class doesn't know about session ids).
    /// </summary>
    public string? GetCapturedSessionId(string jobKey)
    {
        return _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;
    }

    /// <summary>
    /// Surface the most recently parsed <c>turn.completed</c> usage snapshot
    /// for a job (captured by <see cref="TryCaptureTurnUsage"/>) along with
    /// the UTC time it was observed and the run's start time. The runner uses
    /// this to mirror per-turn usage onto the agent message bus.
    /// </summary>
    public (ParsedTurnUsage Usage, DateTime ObservedAt, DateTime StartedAt)? GetLastParsedTurnUsage(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return null;
        if (info.LastParsedUsage == null || info.LastParsedUsageAt == null) return null;
        return (info.LastParsedUsage, info.LastParsedUsageAt.Value, info.Execution.StartedAt);
    }

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
        => _modelDiscovery.GetAsync(GetCliPath(), forceRefresh, ct);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
