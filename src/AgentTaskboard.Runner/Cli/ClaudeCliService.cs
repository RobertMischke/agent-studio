using System.Diagnostics;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli.Adapters;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Live rate-limit snapshot derived from Anthropic's <c>rate_limit_event</c>
/// stream-json frames. Captured per-turn while the CLI is running and
/// surfaced via <c>GET /api/tasks/{id}/claude/session-info</c> so the
/// frontend's protocol-pane pill can show "5h reset in 12 min".
/// </summary>
public record ClaudeRateLimitSnapshot(
    string? Window,
    string? Status,
    long ResetsAt,
    string? OverageStatus,
    bool IsUsingOverage,
    DateTime CapturedAt);

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
        // claude -p <prompt-as-argv> [-r <s>] [--model <m>]
        //   --output-format stream-json --verbose --dangerously-skip-permissions
        //
        // ADR-0014: prompt is the LAST positional argv, not piped via stdin.
        // The previous stdin-pipe path raced claude-code#771 (Claude reads
        // stdin during init and blocks on a connected pipe whose writer is
        // inherited by the child due to Win32's bInheritHandles=TRUE);
        // dropping stdin redirection AND putting the prompt in argv removes
        // the entire pipe-inheritance surface. Calling claude.exe directly
        // (not the .CMD shim, see ResolveCmdShimToExe) means CreateProcess
        // parses argv via CommandLineToArgvW rather than cmd.exe rules, so
        // the multi-line / quote-rich rendered prompt is preserved verbatim.
        // Windows' command-line length limit is 32767 chars; our rendered
        // prompts are well under that.
        //
        // stream-json emits one NDJSON frame per assistant chunk / tool call /
        // tool result, flushed immediately. With the default text format the
        // CLI buffers its entire reply until the model finishes - that's why
        // the Activity Log used to stay empty for the whole run. --verbose
        // is required by the CLI when stream-json is combined with -p.
        // TransformReadLine() in this class normalises the frames into the
        // marker-line convention the frontend parser already understands.

        // Resolve the binary that will actually be executed. Default is to
        // trust whatever the user's shell PATH points at (matches their
        // tested `claude` invocation in PowerShell). The legacy npm-shim
        // override is opt-in via `ClaudeCli:UseNpmShimProbe=true` for the
        // narrow case where the original ADR-0014 stdin-pipe bug regresses;
        // ADR-0014 follow-up moved the prompt to a positional argv (no
        // more stdin pipe at all), so the .CMD shim is safe to invoke
        // directly on the modern code path.
        var fileName = ResolveClaudeBinary(GetCliPath());

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory
        };

        // ArgumentList vs Arguments: ArgumentList lets .NET escape each arg
        // per the Win32 CommandLineToArgvW rules. Mixing is not allowed
        // (the CLR throws when both are populated). For multi-line / quoted
        // content like the rendered prompt this is the only correct path.
        psi.ArgumentList.Add("-p");

        // Claude Code CLI does not expose a --name flag; sessions are
        // identified by the UUID the CLI itself generates and emits in the
        // first `system` stream-json frame. We only ever pass -r <uuid> to
        // resume an already-captured session - never a pre-generated slug.
        if (resumeSession && !string.IsNullOrWhiteSpace(sessionName) && IsCompatibleSessionName(sessionName))
        {
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(sessionName);
        }

        var normalizedModel = NormalizeModelId(model);
        if (!string.IsNullOrWhiteSpace(normalizedModel))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(normalizedModel);
        }

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        // Inject centrally-managed agent rules as a system-prompt overlay.
        // Using --append-system-prompt-file (vs. --append-system-prompt) keeps
        // the multi-line markdown out of the command-line argument string, and
        // lets the Anthropic CLI cache the system-prompt portion across runs.
        var rulesPath = ResolveAgentRulesPath();
        if (rulesPath != null)
        {
            psi.ArgumentList.Add("--append-system-prompt-file");
            psi.ArgumentList.Add(rulesPath);
        }

        // The prompt is the LAST positional argument. Empty/null prompt
        // would still spawn claude (it would just have no input), so we
        // gate on non-empty to keep the behaviour predictable.
        if (!string.IsNullOrEmpty(prompt))
        {
            psi.ArgumentList.Add(prompt);
        }

        return psi;
    }

    /// <summary>
    /// ADR-0014: Claude does NOT pipe through stdin; the prompt is passed
    /// as the last positional argv (see <see cref="BuildStartInfo"/>).
    /// Returning null tells the base class not to redirect stdin at all,
    /// which is the documented Anthropic workaround for claude-code#771
    /// (Claude reads stdin during init and blocks on a connected pipe).
    ///
    /// <para>
    /// The previous behaviour - return <paramref name="prompt"/> so the
    /// base class would pipe-then-close stdin - was the documented way
    /// to bypass cmd.exe's argv truncation, but that mitigation became
    /// unnecessary once ADR-0011 routed us through claude.exe directly
    /// (no cmd.exe wrapping, so multi-line argv is fine).
    /// </para>
    /// </summary>
    protected override string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model)
        => null;

    /// <summary>
    /// Pre-spawn self-heal for the npm-shim install. Probe first; if it
    /// works (the common case) we return immediately so the hook adds no
    /// measurable latency. Only when <c>--version</c> fails do we invoke
    /// <see cref="NpmShimHealer.TryHealClaudeAsync"/>, which restores
    /// atomic-rename orphans, re-runs the wrapper postinstall, and
    /// re-verifies with a fresh <c>--version</c> call. Failure here lets
    /// the spawn abort with a real error message instead of producing yet
    /// another silent 3a-failed-pickup entry.
    /// </summary>
    protected override async Task<(bool Ok, string? Error)> EnsureCliHealthyAsync(CancellationToken ct)
    {
        var probe = TestCliPath();
        if (probe.Available) return (true, null);

        _logger.LogWarning(
            "claude --version failed pre-spawn at '{Path}'; running NpmShimHealer", probe.Path);

        var outcome = await NpmShimHealer.TryHealClaudeAsync(_logger, ct);
        if (outcome.Actions.Count > 0)
        {
            _logger.LogInformation(
                "NpmShimHealer actions for claude: {Actions}", string.Join("; ", outcome.Actions));
        }
        if (!outcome.Available)
        {
            return (false,
                outcome.Error ?? "NpmShimHealer reported claude as unavailable after repair pass");
        }

        // Heal reported success; re-probe via the same code path the spawn will
        // use, so a stale resolver cache or PATH quirk surfaces here, not later.
        var verify = TestCliPath();
        return verify.Available
            ? (true, null)
            : (false, $"claude --version still failing after heal at '{verify.Path}'");
    }

    /// <summary>
    /// Bridge to <see cref="ClaudeEventAdapter"/>. Each raw stdout line is
    /// passed through and emitted on <see cref="CliExecutionServiceBase.OnRunEvent"/>
    /// alongside the legacy marker stream. Stderr passes through unchanged
    /// (we do not parse provider stderr today).
    /// </summary>
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
    {
        if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
        return ClaudeEventAdapter.Map(line.Text, jobKey);
    }

    /// <summary>
    /// ADR-0014 follow-up (Survey § R5): when the
    /// <c>ClaudeCli:UseHandleScrub</c> config flag is true, spawn via
    /// <see cref="Win.WindowsHandleScrubSpawner"/> on Windows. The
    /// flag is OFF by default - the first integration of this code
    /// path (commit c5cfc63) broke init-frame delivery in live
    /// ASP.NET hosting (hStdInput=NULL gave the child an
    /// INVALID_HANDLE_VALUE), and even after the NUL-handle fix the
    /// behaviour needs a deterministic regression test before it can
    /// ship to production. Until the flag flips on, ClaudeCliService
    /// uses the base-class <see cref="Process.Start"/> path with the
    /// R1 (default-deny stdin) + R2 (env hardening) fixes from
    /// ADR-0014.
    ///
    /// <para>
    /// On non-Windows or when the flag is off, falls through to the
    /// base class.
    /// </para>
    /// </summary>
    protected override async Task<ChildHandle> SpawnChildAsync(
        ProcessStartInfo psi,
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model,
        CancellationToken ct)
    {
        var useScrub = string.Equals(
            _configuration["ClaudeCli:UseHandleScrub"], "true",
            StringComparison.OrdinalIgnoreCase);
        if (!useScrub || !OperatingSystem.IsWindows())
        {
            return await base.SpawnChildAsync(psi, prompt, sessionName, resumeSession, model, ct);
        }

        var argList = psi.ArgumentList.ToList();
        var envBlock = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string k && kv.Value is string v) envBlock[k] = v;
        }
        foreach (var kv in psi.Environment) envBlock[kv.Key] = kv.Value;

        var result = Win.WindowsHandleScrubSpawner.Spawn(
            exePath: psi.FileName,
            argList: argList,
            cwd: psi.WorkingDirectory,
            envBlock: envBlock,
            wantStdin: psi.RedirectStandardInput);

        var stdoutReader = new StreamReader(result.Stdout, System.Text.Encoding.UTF8, leaveOpen: false);
        var stderrReader = new StreamReader(result.Stderr, System.Text.Encoding.UTF8, leaveOpen: false);
        Stream stdin = result.Stdin ?? Stream.Null;

        Action<RunStopReason> kill = _ => result.KillTree();

        _logger.LogInformation(
            "[handle-scrub] Spawned {Cli} via STARTUPINFOEX (PID {Pid})",
            CliType, result.Process.Id);

        return new ChildHandle(
            Process: result.Process,
            Stdin: stdin,
            Stdout: stdoutReader,
            Stderr: stderrReader,
            KillOverride: kill);
    }

    /// <summary>
    /// Walk the npm-shim convention to find the underlying claude.exe when
    /// <see cref="GetCliPath"/> resolved to the <c>claude.CMD</c> dispatcher.
    ///
    /// <para>
    /// <b>Why this exists.</b> npm-installed Node CLIs ship as a tiny <c>.CMD</c>
    /// batch shim that calls <c>node.exe path\to\bin\<cli>.exe %*</c>. When the
    /// .NET runner spawns the <c>.CMD</c>, Windows wraps it as
    /// <c>cmd.exe /c "claude.CMD ..."</c> — and that wrapper interferes with
    /// stdin pipe inheritance: claude reads its first <c>system/init</c> frame
    /// out, then never sees the prompt bytes (cmd.exe consumes / mistakes the
    /// pipe), so the agent goes silent and the watchdog kills it. Calling the
    /// real <c>claude.exe</c> directly bypasses cmd.exe entirely. The
    /// regression test
    /// <c>CliSpawnIntegrationTests.DirectExe_PipeStdin_StreamJson_ProducesMultipleFrames</c>
    /// pins this behaviour.
    /// </para>
    /// <para>
    /// We probe the canonical npm-installed location first; if it is missing
    /// (e.g. a portable install or a non-standard layout) we fall back to the
    /// original path and accept that the user may need to set
    /// <c>ClaudeCli:Path</c> explicitly.
    /// </para>
    /// </summary>
    internal static string ResolveCmdShimToExe(string cmdOrExePath)
    {
        if (string.IsNullOrWhiteSpace(cmdOrExePath)) return cmdOrExePath;
        if (!cmdOrExePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && !cmdOrExePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return cmdOrExePath;
        var dir = Path.GetDirectoryName(cmdOrExePath) ?? string.Empty;
        var candidate = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return File.Exists(candidate) ? candidate : cmdOrExePath;
    }

    /// <summary>
    /// Resolves the actual file to execute for the Claude CLI.
    ///
    /// <para>
    /// <b>Default (shell-PATH wins):</b> trust the user's PATH the same way
    /// their `claude` invocation in PowerShell / cmd does. ResolveExecutable
    /// walks PATH + PATHEXT and returns the first hit (typically a native
    /// `claude.exe` from the Anthropic standalone installer, or the
    /// `claude.cmd` shim from an npm install). Both are safe to spawn
    /// directly now that ADR-0014 routes the prompt as a positional argv
    /// instead of through stdin — the original cmd.exe pipe-inheritance
    /// bug no longer applies.
    /// </para>
    ///
    /// <para>
    /// <b>Legacy npm-shim probe</b> (opt-in via
    /// <c>ClaudeCli:UseNpmShimProbe=true</c>): if PATH resolves to a `.cmd`,
    /// look for a sibling `node_modules/@anthropic-ai/claude-code/bin/claude.exe`
    /// and prefer it. Kept as an escape hatch in case argv quoting through
    /// cmd.exe regresses on a specific Windows build; the user can flip the
    /// switch in appsettings without redeploying.
    /// </para>
    ///
    /// <para>
    /// <b>Why this changed:</b> the previous implementation hard-coded the
    /// npm-shim probe and silently picked the node_modules-bundled
    /// `claude.exe` over the user's PATH binary. When the bundled exe was
    /// missing, outdated, or pointed at a different Anthropic release than
    /// the user's shell, project-level chat broke while shell `claude`
    /// kept working — exactly the symptom the user reported.
    /// </para>
    /// </summary>
    internal string ResolveClaudeBinary(string nameOrPath)
    {
        // 1. Shell PATH resolution (uses PATHEXT on Windows).
        var resolved = ResolveExecutable(nameOrPath);

        // 2. Opt-in legacy probe — only kicks in for .cmd / .bat hits.
        var useShim = string.Equals(
            _configuration["ClaudeCli:UseNpmShimProbe"], "true",
            StringComparison.OrdinalIgnoreCase);
        if (useShim)
        {
            var probed = ResolveCmdShimToExe(resolved);
            if (!string.Equals(probed, resolved, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[claude-bin] Legacy npm-shim probe enabled; using {Probed} instead of shell-resolved {Shell}",
                    probed, resolved);
                return probed;
            }
        }

        _logger.LogInformation(
            "[claude-bin] Using shell-resolved binary {Path} (input: {Input})",
            resolved, nameOrPath);
        return resolved;
    }

    /// <summary>
    /// Resolves <c>AgentRules:CorePath</c> to an absolute existing file path.
    /// Honours absolute paths verbatim; for relative paths, searches CWD,
    /// then walks up from <c>AppContext.BaseDirectory</c> looking for the file.
    /// Returns <c>null</c> if no candidate exists or the file is empty / oversized.
    /// </summary>
    private string? ResolveAgentRulesPath()
    {
        var configured = _configuration["AgentRules:CorePath"];
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var candidates = new List<string>();
        if (Path.IsPathRooted(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            candidates.Add(Path.GetFullPath(configured));
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                candidates.Add(Path.Combine(dir.FullName, configured));
                dir = dir.Parent;
            }
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var size = new FileInfo(candidate).Length;
            if (size == 0) return null;
            if (size > 8 * 1024)
            {
                _logger.LogWarning("Agent rules file {Path} is {Size} bytes (>8 KB), skipping injection", candidate, size);
                return null;
            }
            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Translates a single stream-json NDJSON frame from the Claude CLI into
    /// one or more human-readable marker lines, e.g.
    /// <c>● Read /path/to/file</c> or <c>● Edit src/foo.ts</c>. The format
    /// matches what the frontend's <c>activity-log.parser</c> already knows
    /// how to classify, so no per-CLI parser is needed.
    ///
    /// <para>
    /// The frame-mapping logic itself lives in the pure, dependency-free
    /// <see cref="Rendering.ClaudeOutputRenderer"/> (ADR-0013 marker-line twin
    /// of the <c>*EventAdapter</c> classes). This override is a thin delegate so
    /// the renderer can be unit-tested with a plain <c>new()</c> - no process,
    /// no constructor graph - and a new CLI plugs in by implementing
    /// <see cref="Rendering.ICliOutputRenderer"/>. Session-id capture stays in
    /// <c>OnOutputLine</c> below; it reads the rendered <c>● Session</c> marker.
    /// </para>
    /// </summary>
    public override IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
        => _renderer.Render(raw);

    private static readonly Rendering.ClaudeOutputRenderer _renderer = new();

    // Claude's `-r` flag expects a session UUID written by the CLI itself.
    // Slug-style names from another CLI (e.g. Copilot's "taskboard-...") cause
    // the process to hang instead of erroring out, so reject anything that
    // isn't a 36-char canonical UUID.
    private static readonly Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

    public override bool IsCompatibleSessionName(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName) && UuidRegex.IsMatch(sessionName);

    // The first `system` frame is rendered by TransformReadLine into
    //   "● Session init <uuid>"  (or another subtype + uuid)
    // and we read the UUID back from the marker line so the same plumbing as
    // Gemini/Codex applies. Without this, Continue always starts a fresh
    // session because info.SessionName never advances past the placeholder
    // slug TaskRunnerService pre-generates.
    private static readonly Regex SessionMarkerRegex = new(
        @"●\s*Session\s+\S+\s+(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    // Defensive fallback: any canonical UUID anywhere on an early stdout
    // line is treated as the session id. The marker regex above is the
    // intended path, but in production we have observed runs where the
    // marker did not get captured (Claude Code's stream-json frame format
    // varies across versions and platforms). Once we have ANY UUID for
    // this run we stop, so this never overrides the marker if the marker
    // already fired.
    private static readonly Regex AnyUuidRegex = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex RateLimitMarkerRegex = new(
        @"●\s*Rate limit\b.*\[" +
        @"window=(?<win>[^\s\]]+)\s+" +
        @"status=(?<st>[^\s\]]+)\s+" +
        @"resetsAt=(?<reset>\d+)\s+" +
        @"overage=(?<ov>[^\s\]]+)\s+" +
        @"usingOverage=(?<using>true|false)\]",
        RegexOptions.Compiled);

    protected override void OnOutputLine(ProcInfo info, CliOutputLine line)
    {
        if (line.Text == null) return;

        // Capture session UUID. Marker line is the intended path, but we
        // also accept ANY canonical UUID on any stdout line as a defensive
        // fallback, because the marker has been observed to be missing on
        // some Claude Code stream-json versions / platforms. The first
        // captured UUID wins; later UUIDs in the same run (e.g. tool
        // result ids) are ignored so we do not overwrite the session id
        // with an unrelated identifier.
        if (info.CapturedSessionId == null && line.Stream == "stdout")
        {
            var sessionMatch = SessionMarkerRegex.Match(line.Text);
            string? uuid = sessionMatch.Success ? sessionMatch.Groups["uuid"].Value : null;
            if (uuid == null)
            {
                var anyUuidMatch = AnyUuidRegex.Match(line.Text);
                if (anyUuidMatch.Success) uuid = anyUuidMatch.Value;
            }
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                info.CapturedSessionId = uuid;
                info.SessionName = uuid;
                _logger.LogInformation("Captured Claude session id {Id} (marker={Marker})",
                    uuid, sessionMatch.Success);
            }
        }

        // Capture the latest rate-limit telemetry from the bracketed kv tail
        // of the `● Rate limit ... [window=... status=... resetsAt=...]` marker.
        var rateMatch = RateLimitMarkerRegex.Match(line.Text);
        if (rateMatch.Success)
        {
            long.TryParse(rateMatch.Groups["reset"].Value, out var resetsAt);
            info.LastRateLimit = new ClaudeRateLimitSnapshot(
                Window:         NullIfPlaceholder(rateMatch.Groups["win"].Value),
                Status:         NullIfPlaceholder(rateMatch.Groups["st"].Value),
                ResetsAt:       resetsAt,
                OverageStatus:  NullIfPlaceholder(rateMatch.Groups["ov"].Value),
                IsUsingOverage: rateMatch.Groups["using"].Value == "true",
                CapturedAt:     DateTime.UtcNow);
        }
    }

    private static string? NullIfPlaceholder(string v) =>
        string.IsNullOrEmpty(v) || v == "?" || v == "-" ? null : v;

    public string? GetCapturedSessionId(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? info.CapturedSessionId : null;

    public ClaudeRateLimitSnapshot? GetLastRateLimit(string jobKey)
        => _processes.TryGetValue(jobKey, out var info) ? info.LastRateLimit : null;

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        // No live discovery yet — surface the well-known Claude 4.x family. The
        // user picks one, the CLI validates. Empty list also works (default).
        var models = new List<CliModelInfo>
        {
            new() { Id = "claude-opus-4-8",       Label = "Claude Opus 4.8",     Vendor = "anthropic", IsDefault = true },
            new() { Id = "claude-opus-4-7",       Label = "Claude Opus 4.7",     Vendor = "anthropic" },
            new() { Id = "claude-sonnet-4-6",     Label = "Claude Sonnet 4.6",   Vendor = "anthropic" },
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
