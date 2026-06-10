using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Cli;

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
    internal const string FallbackModel = ModelIds.Gpt5Codex;

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

    /// <summary>
    /// T1b (ASS-1742): Codex isolates clean runs via <c>CODEX_HOME</c>, which
    /// relocates the whole <c>~/.codex</c> home (auth, config, sessions,
    /// history) to a per-run temp dir. <see cref="PrepareCleanContext"/> seeds
    /// only the auth token + base config so auth still works while the run sees
    /// no leftover session history.
    /// </summary>
    public override bool SupportsCleanContext => true;

    public override CleanContextPreparation? PrepareCleanContext(string workingDirectory)
        => CleanContextPreparer.PrepareCodex(ResolveUserHome(), _logger);

    protected override ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel,
        string? permissionMode)
    {
        // For Codex, sessionName is the session UUID (or null for a fresh session).
        // codex exec [resume <uuid>] [--experimental-json] [-m <model>] -
        //
        // 2026-05-12: Codex 0.130 changed positional-PROMPT semantics so a
        // rules-heavy prompt got interpreted as "initial instructions" and
        // the model answered `[[TASK_NOOP]]` ("no actionable task provided")
        // — the entire prompt was consumed as a system-side header. Switching
        // to `-` (read instructions from stdin) restores the user-message
        // path: Codex blocks on stdin, we write the full prompt + system
        // prefix, then close stdin. The model then sees the prompt as the
        // actual user turn and acts on it.
        //
        // Reproduced on Sternstunde batch + 3 Agent TP Codex jobs; manual
        // verification under `< NUL` confirms positional NOOPs even on
        // simple tasks once the prompt has a few "Rules for this run" lines.
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(GetCliPath()),
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add("exec");

        // IMPORTANT - argument ORDER vs the `resume` subcommand.
        // In the codex CLI, exec options must precede the `resume`
        // subcommand. Only `--model`/`-m`, the bypass flag, and `--json`
        // are marked clap-`global` and therefore tolerate either position;
        // crucially `--sandbox` is an EXEC-level option that is NOT global,
        // so `codex exec resume <id> --sandbox danger-full-access` fails with
        // `error: unexpected argument '--sandbox' found` (exitCode 2), which
        // broke EVERY codex resume / crash-recovery into a relaunch loop
        // (observed 2026-06-09 on a re-/start of an interrupted task). We
        // therefore emit ALL option flags here, BEFORE adding `resume`, so
        // they bind to `exec` where they are valid.

        // --experimental-json is the SDK-backed exec protocol: stdout stays
        // machine-readable, while completion is the process exit after the
        // stream closes, not a model-authored sentinel.
        psi.ArgumentList.Add("--experimental-json");

        // Sandbox posture is resolved per-project (default YOLO ==
        // --sandbox danger-full-access). This replaces the global
        // ~/.codex/config.toml sandbox_mode stop-gap: a null mode normalizes to
        // YOLO so the danger-full-access default holds even without the file.
        foreach (var flag in CliPermissionFlags.For(CliType, permissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);
        }

        foreach (var flag in CliReasoningFlags.For(CliType, model, thinkingLevel))
            psi.ArgumentList.Add(flag);

        // The `resume <session-id>` subcommand comes AFTER the exec options
        // above (see the ORDER note). On a resume the prompt positional
        // belongs to the resume subcommand; on a fresh run it belongs to exec.
        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName))
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(sessionName);
        }

        // Use `-` to tell Codex to read the prompt from stdin instead of
        // taking it as a positional argv. The actual bytes are written by
        // the base class via GetPromptStdinPayload below.
        if (!string.IsNullOrEmpty(prompt))
        {
            psi.ArgumentList.Add("-");
        }

        return psi;
    }

    protected override string? NormalizeModelForInvocation(string? model)
        => ResolveInvocationModel(model, _configuration);

    internal static string ResolveInvocationModel(string? model, IConfiguration configuration)
    {
        var requested = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (!IsForeignModelId(requested)) return requested ?? DefaultCodexModel(configuration);

        return DefaultCodexModel(configuration);
    }

    private static string DefaultCodexModel(IConfiguration configuration)
    {
        var configured = configuration["CodexCli:Model"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        configured = configuration["CodexCli:DefaultModel"]?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? FallbackModel : configured;
    }

    internal ProcessStartInfo BuildStartInfoForTest(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model,
        string? thinkingLevel = null,
        string? permissionMode = null)
        => BuildStartInfo(
            prompt,
            workingDirectory,
            sessionName,
            resumeSession,
            NormalizeModelForInvocation(model),
            thinkingLevel,
            permissionMode);

    internal string? BuildPromptStdinPayloadForTest(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => GetPromptStdinPayload(
            prompt,
            sessionName,
            resumeSession,
            NormalizeModelForInvocation(model));

    private static bool IsForeignModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        return model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
               || model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
               || model.StartsWith("copilot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Codex has no <c>--append-system-prompt</c> flag (Claude's mechanism),
    /// so per-CLI orchestrator guidance must be prepended to the positional
    /// prompt argument. This builds a short prefix with two prophylactic
    /// hints that complement the reactive
    /// <see cref="AgentStudio.Cli.AgentEnvironmentDetector"/>
    /// pipeline:
    /// <list type="number">
    ///   <item>Sentinel reminder. Codex's pass-through frame model means the
    ///         fresh-start template's terminal-sentinel rule can drift out of
    ///         view on a resume turn, where the user follow-up is the entire
    ///         prompt. The "missing-terminal-sentinel" auto-review case noted
    ///         in <c>AgentEnvironmentDetector</c>'s "why this exists" section
    ///         was caused by exactly this gap.</item>
    ///   <item>No-shell hint on Windows. Codex's Windows sandbox wrapper
    ///         (<c>windows-sandbox-rs</c>) refuses <c>CreateProcessAsUserW</c>
    ///         under common service / RDP logon-session configurations; the
    ///         agent retries the same command 3-10 times and burns the silence
    ///         budget without producing useful output. Telling Codex up front
    ///         to prefer file reads and to report a single failure via
    ///         <c>[[TASK_BLOCKED:windows-sandbox]]</c> short-circuits that
    ///         retry loop.</item>
    /// </list>
    /// Kept deliberately short (~5 lines): every Codex invocation, including
    /// resumes whose user prompt is one sentence, pays this prefix in tokens.
    /// </summary>
    internal static string BuildSystemPromptPrefix(bool isWindows)
    {
        const string sentinelLine =
            "Orchestrator note: your reply MUST end with exactly one of `[[TASK_DONE]]`, " +
            "`[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, or " +
            "`[[TASK_NOOP]]` as the final line - this is required, not optional. The " +
            "orchestrator parses this token; without it the run lands in auto-review as " +
            "missing-terminal-sentinel.";

        const string investigationLine =
            "Time-box investigation: do not spend the whole turn searching or reading - " +
            "form a plan early and start making the change, then verify. A turn spent only " +
            "exploring will be killed by the watchdog with the work unfinished.";

        if (!isWindows)
        {
            return sentinelLine + "\n" + investigationLine + "\n\n";
        }

        const string windowsShellLine =
            "Windows note: if a shell command returns `windows sandbox: runner error` " +
            "or `CreateProcessAsUserW failed`, do NOT retry; the host sandbox is " +
            "refusing execution. Read files directly instead, and if you cannot make " +
            "progress without shell access, stop and reply with " +
            "`[[TASK_BLOCKED:windows-sandbox]]`.";

        return sentinelLine + "\n" + investigationLine + "\n" + windowsShellLine + "\n\n";
    }

    /// <summary>
    /// Deliver the full prompt (system-prompt prefix + rendered template) to
    /// Codex via stdin. <see cref="BuildStartInfo"/> passes <c>-</c> as the
    /// positional, telling Codex to read instructions from stdin; the base
    /// class then writes this payload and closes the pipe so Codex sees EOF
    /// after the prompt and starts the turn. This is the only delivery path
    /// that survived Codex 0.130's stricter positional-PROMPT semantics
    /// without provoking <c>[[TASK_NOOP]]</c>.
    /// </summary>
    protected override string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => string.IsNullOrEmpty(prompt)
            ? null
            : BuildSystemPromptPrefix(OperatingSystem.IsWindows()) + prompt;

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
            TryCaptureSessionId(info, line);
            TryCaptureCommandExecution(info, line);
        }

        return CodexEventAdapter.Map(line.Text, jobKey);
    }

    /// <summary>
    /// Inputs the runner's per-tick silent-completion check needs. Returns
    /// <c>null</c> when no <c>command_execution</c> <c>item.completed</c>
    /// has been observed yet for this run. Mirrors the
    /// <see cref="GetLastParsedTurnUsage"/> shape: pure read on top of the
    /// per-CLI capture done inside <see cref="MapLineToRunEvents"/>.
    /// </summary>
    public CodexLastCommandSnapshot? GetLastCommandExecution(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return null;
        if (info.LastCommandObservedAt is null) return null;
        return new CodexLastCommandSnapshot(
            ExitCode: info.LastCommandExitCode,
            Command: info.LastCommandLine,
            OutputTail: info.LastCommandOutputTail,
            ObservedAt: info.LastCommandObservedAt.Value);
    }

    /// <summary>True once the per-tick silent-completion detector tripped for this run.</summary>
    public bool IsSilentCompletionTripped(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) && info.SilentCompletionTripped;

    /// <summary>
    /// Last <c>command_execution</c> <c>item.completed</c> frame the run
    /// emitted. Carried as a value type because the runner reads it from a
    /// different thread than the read loop that wrote it; the snapshot is
    /// immutable so no copy-coupling exists between producer and consumer.
    /// </summary>
    public readonly record struct CodexLastCommandSnapshot(
        int? ExitCode,
        string? Command,
        string? OutputTail,
        DateTime ObservedAt);

    /// <summary>
    /// Pre-parse <c>item.completed</c> frames whose nested item is a
    /// <c>command_execution</c> and stash the trigger data on
    /// <see cref="ProcInfo"/>. The runner reads this via
    /// <see cref="GetLastCommandExecution"/> to feed
    /// <see cref="CodexSilentCompletionDetector"/>.
    /// <para>
    /// Best-effort: a malformed frame leaves the previous snapshot
    /// untouched. Fast prefilter keeps the hot path cheap - most stdout
    /// lines never reach <see cref="JsonDocument.Parse"/>.
    /// </para>
    /// </summary>
    private static void TryCaptureCommandExecution(ProcInfo info, CliOutputLine line)
    {
        var parsed = TryExtractCommandExecution(line.Text);
        if (parsed is not { } cap) return;

        info.LastCommandExitCode = cap.ExitCode;
        info.LastCommandLine = cap.Command;
        info.LastCommandOutputTail = cap.OutputTail;
        info.LastCommandObservedAt = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp;
    }

    /// <summary>
    /// Pure JSON parser for the silent-completion capture path. Exposed
    /// <c>internal</c> so the regression test for the Codex 0.128
    /// <c>command_execution</c> frame shape can drive it without spinning
    /// up a live CLI. Returns <c>null</c> for any non-matching line shape
    /// (other frame type, missing <c>item</c>, malformed JSON, non-JSON
    /// text) so the caller's hot path stays cheap and a malformed frame
    /// never throws.
    /// </summary>
    internal static (int? ExitCode, string? Command, string? OutputTail)? TryExtractCommandExecution(string? line)
    {
        var text = line?.TrimStart();
        if (string.IsNullOrEmpty(text) || text![0] != '{') return null;
        if (!text.Contains("item.completed", StringComparison.Ordinal)) return null;
        if (!text.Contains("command_execution", StringComparison.Ordinal)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "item.completed", StringComparison.Ordinal)) return null;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
            if (!string.Equals(itemType, "command_execution", StringComparison.Ordinal)) return null;

            int? exitCode = null;
            if (item.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var ecValue))
                exitCode = ecValue;

            string? command = null;
            if (item.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                command = cmd.GetString();

            string? outputTail = null;
            if (item.TryGetProperty("aggregated_output", out var agg) && agg.ValueKind == JsonValueKind.String)
            {
                var raw = agg.GetString() ?? string.Empty;
                outputTail = raw.Length <= 400 ? raw : raw[^400..];
            }

            return (exitCode, command, outputTail);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Codex emits the session UUID on the first <c>{"type":"thread.started",
    /// "thread_id":"&lt;uuid&gt;"}</c> line of <c>--json</c> output (codex-cli
    /// &gt;= 0.128). Older builds used <c>{"type":"session_meta","payload":{"id":"&lt;uuid&gt;"}}</c>
    /// which we still accept. Without this capture the per-job session store
    /// stays empty and every follow-up rebuilds context from disk via Recovery
    /// instead of <c>codex exec resume &lt;uuid&gt;</c>, throwing away Codex's
    /// own prompt-cache.
    /// <para>
    /// This runs in <see cref="MapLineToRunEvents"/> on the RAW stdout line, not
    /// in <c>OnOutputLine</c>. <c>OnOutputLine</c> now receives the rendered
    /// <c>● Session &lt;id&gt;</c> marker (see <see cref="CodexOutputRenderer"/>),
    /// from which the original <c>thread_id</c> payload is no longer recoverable;
    /// capturing here keeps <see cref="TryExtractSessionId"/> reading the real
    /// JSON frame.
    /// </para>
    /// </summary>
    private void TryCaptureSessionId(ProcInfo info, CliOutputLine line)
    {
        if (info.CapturedSessionId != null) return;

        var id = TryExtractSessionId(line.Text);
        if (id == null) return;

        info.CapturedSessionId = id;
        info.SessionName ??= id;
        _logger.LogInformation("Captured Codex session id {Id}", id);
    }

    /// <summary>
    /// Translate a single <c>codex exec --experimental-json</c> JSONL frame into the marker
    /// vocabulary the frontend activity-log parser classifies, e.g.
    /// <c>● Run &lt;cmd&gt;</c> or <c>● Edit &lt;path&gt;</c>. Delegates to the pure,
    /// dependency-free <see cref="CodexOutputRenderer"/> (the
    /// marker-line twin of <see cref="CodexEventAdapter"/>). Before this existed
    /// Codex had no override and raw JSONL leaked into the Activity Log.
    /// </summary>
    public override IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
        => _renderer.Render(raw);

    private static readonly CodexOutputRenderer _renderer = new();

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
        catch (JsonException __ex) { SilentCatch.Note(__ex, "CodexCliService: malformed frame; nothing to capture"); /* malformed frame; nothing to capture */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Codex turn-usage capture skipped"); }
    }

    /// <summary>
    /// Parses a single <c>codex exec --experimental-json</c> stdout line and returns the
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
