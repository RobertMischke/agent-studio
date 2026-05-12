using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Shared process-orchestration logic for the slim non-Copilot CLI backends
/// (Claude Code, Codex). Handles spawning, output streaming, persistence, and
/// reattach. Subclasses provide the CLI-specific argument-building and
/// session-name handling via <see cref="BuildStartInfo"/>.
/// <para>
/// CopilotCliService predates this base and keeps its own (heavily customised)
/// implementation — it shares the public <see cref="ICliExecutionService"/>
/// surface but not the code path. Refactoring it into this base would be a
/// pure churn change with no behavioural win.
/// </para>
/// </summary>
public abstract class CliExecutionServiceBase : ICliExecutionService
{
    protected readonly ILogger _logger;
    protected readonly IConfiguration _configuration;
    protected readonly ConcurrentDictionary<string, ProcInfo> _processes = new();

    public abstract string CliType { get; }

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;

    /// <summary>
    /// Typed lifecycle events from the CLI (ADR-0013). Subclasses with an
    /// adapter (Claude / Codex / Gemini / Copilot) raise these alongside
    /// the legacy <see cref="OnOutput"/> stream so consumers can migrate
    /// incrementally. Subclasses without an adapter emit nothing here -
    /// the runner falls back to the silence-only watchdog in that case.
    /// </summary>
    public event Action<string, CliRunEvent>? OnRunEvent;

    /// <summary>
    /// Subclass entry point for emitting typed events. Wraps the public
    /// invocation with a per-subscriber try/catch so a buggy listener
    /// cannot crash the read loop.
    /// </summary>
    protected void RaiseRunEvent(string jobKey, CliRunEvent evt)
    {
        try { OnRunEvent?.Invoke(jobKey, evt); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnRunEvent subscriber threw for {JobId}", jobKey); }
    }

    protected CliExecutionServiceBase(ILogger logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public abstract string GetCliPath();

    /// <summary>
    /// Default: accept any non-empty session name. Subclasses with strict
    /// session-id formats (Claude requires UUIDs) override to reject names
    /// that came from a different CLI's session store.
    /// </summary>
    public virtual bool IsCompatibleSessionName(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName);

    public virtual (bool Available, string? Version, string Path) TestCliPath(string? path = null)
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
            var rawVersion = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            // Keep only the first non-empty line — some CLIs print update hints on line 2+
            var version = rawVersion.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return (proc.ExitCode == 0, version, testPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI not available at path '{Path}'", testPath);
            return (false, null, testPath);
        }
    }

    public bool IsAvailable() => TestCliPath().Available;

    /// <summary>
    /// On Windows, npm-installed Node CLIs ship as a Bash shim (no extension) plus
    /// a <c>.cmd</c> launcher. <see cref="Process.Start"/> can only execute the
    /// <c>.cmd</c>/<c>.exe</c>, so we resolve bare names to their PATHEXT match.
    /// On non-Windows the input is returned unchanged.
    /// </summary>
    public static string ResolveExecutable(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return nameOrPath;
        if (!OperatingSystem.IsWindows()) return nameOrPath;
        // Already absolute or has an extension — trust the caller.
        if (Path.IsPathRooted(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;
        if (Path.HasExtension(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        // If a path was given (rooted or relative with extension), keep it.
        if (Path.IsPathRooted(nameOrPath))
        {
            foreach (var ext in exts)
            {
                var candidate = nameOrPath + ext;
                if (File.Exists(candidate)) return candidate;
            }
            return nameOrPath;
        }

        foreach (var dir in dirs)
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, nameOrPath + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return nameOrPath;
    }

    /// <summary>Subclass hook: build the actual command-line for this CLI.</summary>
    protected abstract ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model);

    /// <summary>
    /// Subclass hook: return the text the runner should write to the child's
    /// stdin instead of (or after) closing it. Default null means "close
    /// stdin immediately" - the legacy behavior. Claude overrides this so
    /// the multi-KB rendered prompt gets piped through stdin instead of
    /// embedded in argv, which on Windows is the difference between the
    /// agent seeing the whole prompt and the agent seeing only the heading
    /// (cmd.exe truncates long quoted args at the first newline / over the
    /// length limit). When this returns non-null, BuildStartInfo MUST have
    /// omitted the prompt from argv.
    /// </summary>
    protected virtual string? GetPromptStdinPayload(
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model) => null;

    /// <summary>Subclass hook: try to extract session metadata from a fresh output line.</summary>
    protected virtual void OnOutputLine(ProcInfo info, CliOutputLine line) { }

    /// <summary>
    /// Subclass hook: map one raw stdout/stderr line to zero or more
    /// <see cref="CliRunEvent"/> instances. Default: yield nothing (CLIs
    /// without an adapter stay on the silence-only watchdog). Subclasses
    /// with an adapter (Claude / Codex / Gemini) override and delegate
    /// to the per-CLI mapping function.
    ///
    /// <para>
    /// The base class fires <see cref="OnRunEvent"/> for every event
    /// returned here, in order, on the same read-loop thread. Adapters
    /// must be pure functions and not throw - exceptions are swallowed
    /// so a malformed frame cannot crash the read loop.
    /// </para>
    /// </summary>
    protected virtual IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
        => Array.Empty<CliRunEvent>();

    /// <summary>
    /// Subclass hook: translate a single raw line read from the CLI's stdout
    /// or stderr into one or more user-visible buffer lines. Default: pass
    /// through unchanged. Used by <see cref="ClaudeCliService"/> to expand
    /// stream-json NDJSON frames into the marker-line convention the
    /// frontend's activity log parser already understands.
    /// </summary>
    public virtual IEnumerable<CliOutputLine> TransformReadLine(CliOutputLine raw)
    {
        yield return raw;
    }

    public virtual Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        return Task.FromResult(new CliModelCatalog
        {
            Models = [],
            Source = "default-only",
            FetchedAt = DateTime.UtcNow
        });
    }

    public async Task<(CliExecution? Execution, string? Error)> StartAsync(
        string jobId,
        string jobKey,
        string prompt,
        string workingDirectory,
        string? sessionName = null,
        bool resumeSession = false,
        string? model = null,
        string? jobFolderPath = null,
        CancellationToken ct = default)
    {
        if (_processes.TryGetValue(jobKey, out var existing))
        {
            if (!existing.Process.HasExited)
                return (null, $"{CliType} CLI process already running for job '{jobId}'");
            _processes.TryRemove(jobKey, out _);
        }

        var psi = BuildStartInfo(prompt, workingDirectory, sessionName, resumeSession, model);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        // ADR-0014: stdin is default-deny. We only redirect (and pipe a
        // payload through) when the per-CLI subclass returns a non-empty
        // GetPromptStdinPayload. The dominant suspect for the live
        // "agent silent after init" hang (claude-code#771 plus convergent
        // OSS evidence) is exactly this: a connected stdin pipe inherited
        // by a CLI that reads stdin during init, on Windows ASP.NET
        // hosting where the parent's writer-end close races the child's
        // first read. When no payload is needed, the child inherits the
        // parent's already-non-interactive stdin and the race goes away.
        var stdinPayload = GetPromptStdinPayload(prompt, sessionName, resumeSession, model);
        psi.RedirectStandardInput = !string.IsNullOrEmpty(stdinPayload);
        psi.UseShellExecute = false;
        psi.CreateNoWindow  = true;
        psi.WorkingDirectory = workingDirectory;
        // Force UTF-8 on the redirected streams. Default on Windows is the
        // system code page (CP1252 here), which corrupts non-ASCII bytes from
        // Claude/Codex output and previously caused silent crashes when a
        // prompt contained umlauts. Also tell the child process to emit UTF-8
        // by setting common env hints.
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding  = System.Text.Encoding.UTF8;
        psi.Environment["PYTHONIOENCODING"]   = "utf-8";
        psi.Environment["LC_ALL"]             = "C.UTF-8";
        psi.Environment["LANG"]               = "C.UTF-8";
        // claude-cli is a Node process; this disables Node's BOM/encoding quirks.
        psi.Environment["NODE_NO_WARNINGS"]   = "1";

        // ADR-0014 follow-up: env hardening derived from research/cli-orchestration-survey-2026-05.md
        // section "P5. Pre-emptive trust-store / settings hardening". These
        // env vars suppress interactive auto-update prompts, color escape
        // sequences that confuse our parsers, and tip-of-day banners that
        // would otherwise dominate the activity log. They are CLI-specific
        // but harmless when set globally; setting them in the base class
        // means all four CLIs benefit without per-driver duplication.
        psi.Environment["NO_COLOR"]                       = "1";
        psi.Environment["FORCE_COLOR"]                    = "0";
        psi.Environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"]= "1";
        psi.Environment["GEMINI_NO_UPDATE_NOTIFIER"]      = "1";
        psi.Environment["CODEX_DISABLE_TIP_OF_THE_DAY"]   = "1";
        // CI=1 is the conventional non-interactive marker. Most npm CLIs
        // respect it to skip prompts; harmless for the others.
        psi.Environment["CI"]                             = "1";

        // When running under the agent task orchestrator, set JOB_RESULTS_DIR
        // so tools like Playwright can harvest artifacts into the job folder.
        // Cleaned up in the task orchestrator's result directories.
        if (!string.IsNullOrEmpty(jobFolderPath))
        {
            psi.Environment["JOB_RESULTS_DIR"]            = Path.Combine(jobFolderPath, "results");
        }

        ChildHandle child;
        try
        {
            child = await SpawnChildAsync(psi, prompt, sessionName, resumeSession, model, ct);
            // ADR-0014: only write to stdin when the subclass said it has a
            // payload (and the base class therefore set RedirectStandardInput
            // = true above). When no payload, the child inherits the host's
            // non-interactive stdin (or NUL on a daemon) - same effect as
            // Python's stdin=DEVNULL or Node's stdio:'ignore', which is the
            // documented Anthropic workaround for claude-code#771.
            if (!string.IsNullOrEmpty(stdinPayload))
            {
                try
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(stdinPayload);
                    await child.Stdin.WriteAsync(bytes, ct);
                    await child.Stdin.FlushAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write stdin payload for {Cli} job {JobId}", CliType, jobId);
                }
                finally
                {
                    // Close *only* when we actually opened it. Closing
                    // Stream.Null is a no-op so the guard is defensive
                    // rather than load-bearing.
                    try { child.Stdin.Close(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Cli} CLI for job {JobId}", CliType, jobId);
            return (null, $"Failed to start {CliType} CLI: {ex.Message}");
        }
        var process = child.Process;

        var execution = new CliExecution
        {
            JobId = jobId,
            JobKey = jobKey,
            ProcessId = process.Id,
            StartedAt = DateTime.UtcNow,
            Status = "running",
            Model = string.IsNullOrWhiteSpace(model) ? null : model
        };

        var logPath = GetOutputLogPath(jobKey);
        var info = new ProcInfo(process, execution, workingDirectory)
        {
            OutputLogPath = logPath,
            OutputLog = new CliOutputLogStore(logPath),
            SessionName = sessionName,
            LastStreamedAt = execution.StartedAt,
            KillOverride = child.KillOverride,
            ChildStdin = child.Stdin
        };
        try { info.OutputLog.Reset(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to reset CLI output log {Path}", logPath); }
        _processes[jobKey] = info;

        // Persist the live PID + identity so a startup reaper can kill the
        // process if the backend died before MonitorProcessAsync removed it.
        // Failure here is non-fatal — the worst case is one orphan that the
        // user has to clean up manually after a hard crash.
        try
        {
            UpsertActiveJob(new ActiveJob
            {
                JobKey = jobKey,
                JobId = jobId,
                ProcessId = process.Id,
                ProcessName = SafeProcessName(process),
                ProcessStartTimeUtc = SafeProcessStartTime(process),
                StartedAt = execution.StartedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record active-job entry for {JobId} ({Cli})", jobId, CliType);
        }

        OnStarted?.Invoke(jobKey, execution);
        // ADR-0013: typed event channel. Subclasses with an adapter raise
        // their own events from the read loop; the base class always
        // raises RunStarted so the runner's phase tracker can initialize.
        RaiseRunEvent(jobKey, new CliRunEvent.RunStarted(process.Id, CliType, model) { JobKey = jobKey });

        _logger.LogInformation("Started {Cli} CLI for job {JobId} (PID {Pid}) in {Cwd}",
            CliType, jobId, process.Id, workingDirectory);

        // Synthetic "Started" line so the Activity log isn't empty during the
        // window between spawn and the CLI's first stdout byte. Claude's `-p`
        // mode buffers output until the model finishes — without this, users
        // saw a blank protocol for 30+ seconds and assumed the job was stuck.
        var startedLine = new CliOutputLine
        {
            Timestamp = DateTime.UtcNow,
            Stream = "system",
            Text = $"[taskboard] Started {CliType} CLI (PID {process.Id})"
                   + (string.IsNullOrWhiteSpace(model) ? "" : $", model={model}")
                   + (string.IsNullOrWhiteSpace(sessionName) ? "" : $", session={sessionName}")
                   + (resumeSession ? " (resume)" : "")
        };
        info.OutputBuffer.Add(startedLine);
        if (!info.OutputLog.Append(startedLine))
            _logger.LogWarning("Failed to persist 'started' line for job {JobId} to {Path}", jobId, info.OutputLogPath);
        try { OnOutput?.Invoke(jobKey, startedLine); } catch { }

        var stdoutTask = ReadStreamAsync(jobKey, child.Stdout, "stdout", info, ct);
        var stderrTask = ReadStreamAsync(jobKey, child.Stderr, "stderr", info, ct);
        info.StdoutReadTask = stdoutTask;
        info.StderrReadTask = stderrTask;
        _ = MonitorProcessAsync(jobKey, process, info, ct);

        return (execution, null);
    }

    /// <summary>
    /// Subclass hook: spawn the child process. Default uses
    /// <see cref="System.Diagnostics.Process"/> with redirected pipes.
    /// CLIs whose stdout block-buffers when piped (Node-based ones on Windows:
    /// Claude / Codex / Gemini) override to spawn through a pseudo-terminal so
    /// stream-json frames flush per newline.
    /// </summary>
    protected virtual Task<ChildHandle> SpawnChildAsync(
        ProcessStartInfo psi,
        string prompt,
        string? sessionName,
        bool resumeSession,
        string? model,
        CancellationToken ct)
    {
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        // When stdin is not redirected (ADR-0014 default-deny path),
        // accessing Process.StandardInput throws InvalidOperationException.
        // Hand a no-op stream upstream so the StartAsync flow stays linear.
        var stdin = psi.RedirectStandardInput ? p.StandardInput.BaseStream : Stream.Null;
        return Task.FromResult(new ChildHandle(
            Process: p,
            Stdin: stdin,
            Stdout: p.StandardOutput,
            Stderr: p.StandardError));
    }

    public bool Stop(string jobKey, RunStopReason reason = RunStopReason.UserStop)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        try
        {
            if (!info.Process.HasExited)
            {
                // Record the intent BEFORE Kill so MonitorProcessAsync's
                // classifier can tell the deliberate kill apart from a real
                // crash - even if Kill races the natural exit by a tick, the
                // marker is set and the classifier does the right thing.
                info.StopReason = reason;
                if (info.KillOverride != null)
                {
                    try { info.KillOverride(reason); }
                    catch (Exception ex) { _logger.LogWarning(ex, "PTY KillOverride threw for {JobId}; falling back to Process.Kill", jobKey); }
                }
                else
                {
                    info.Process.Kill(entireProcessTree: true);
                }
                _logger.LogInformation("Killed {Cli} process for job {JobId} (reason={Reason})", CliType, jobKey, reason);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill {Cli} process for job {JobId}", CliType, jobKey);
            return false;
        }
    }

    public bool SendInput(string jobKey, string input)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        if (info.Process.HasExited) return false;
        try
        {
            // Route through the ChildHandle's stdin stream when one was
            // captured at spawn time so PTY-based subclasses don't bypass
            // the pseudo-terminal writer. Fall back to the raw Process.
            // StandardInput for the default pipe path (the StreamWriter
            // wraps the same underlying Stream, so behaviour is identical
            // for non-PTY callers).
            var bytes = System.Text.Encoding.UTF8.GetBytes(input + "\n");
            if (info.ChildStdin != null)
            {
                info.ChildStdin.Write(bytes, 0, bytes.Length);
                info.ChildStdin.Flush();
            }
            else
            {
                info.Process.StandardInput.WriteLine(input);
            }
            return true;
        }
        catch { return false; }
    }

    public List<CliOutputLine> GetOutput(string jobKey)
    {
        if (_processes.TryGetValue(jobKey, out var info))
            return info.OutputBuffer.ToList();

        // No live process. Either the backend was restarted while a CLI run
        // was in flight, or the post-exit retention window elapsed. Recover
        // from the persisted JSONL so the Activity Log isn't blank — this is
        // the durability guarantee callers depend on.
        return CliOutputLogStore.ReadAll(GetOutputLogPath(jobKey));
    }

    public void DiscardPersistedOutput(string jobKey)
    {
        // If the process is still tracked, drop the open writer first so the
        // Windows file handle is released before delete.
        if (_processes.TryGetValue(jobKey, out var info))
        {
            try { info.OutputLog.Dispose(); } catch { /* already disposed */ }
        }

        var path = GetOutputLogPath(jobKey);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete persisted CLI log {Path}", path); }
    }

    public CliExecution? GetExecution(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.Execution : null;

    public SessionUsage? GetLastUsage(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastUsage : null;

    public bool IsRunningForProject(string rootPath) =>
        _processes.Values.Any(p => p.WorkingDirectory == rootPath && !p.Process.HasExited);

    public DateTime? GetLastStreamedAt(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastStreamedAt : null;

    public WatchdogState GetWatchdogState(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastWatchdogState : WatchdogState.Healthy;

    public void SetWatchdogState(string jobKey, WatchdogState state)
    {
        if (_processes.TryGetValue(jobKey, out var info)) info.LastWatchdogState = state;
    }

    /// <summary>
    /// Startup hook. Default behaviour for base-class CLIs (Claude / Codex /
    /// Gemini) is to <b>reap</b> orphaned processes — kill any CLI process that
    /// outlived a previous backend run. We deliberately do not re-attach: the
    /// stdout pipe is unrecoverable, so an orphan would keep mutating the repo
    /// while the user's UI is blind. Killing on startup eliminates the
    /// double-execution risk and lets the resume-prompt logic in
    /// <see cref="ProjectRunner"/> drive a clean fresh continuation.
    /// <para>
    /// Subclasses that genuinely want re-attach semantics (Copilot today) can
    /// override this — Copilot does so in its own service and never enters
    /// this base implementation because it doesn't extend the base class.
    /// </para>
    /// </summary>
    public virtual void ReattachOnStartup() => ReapOrphans();

    /// <summary>
    /// Runs the canonical <see cref="AgentEnvironmentDetector"/> against a
    /// freshly read line. When a recognised OS-level / sandbox blocker
    /// fires often enough (or once for an unambiguous pattern), writes a
    /// synthetic <c>[environment-blocker]</c> system line so
    /// <see cref="Runner.AgentOutcomeAnalyzer"/> can pick it up post-run,
    /// then terminates the child via <see cref="Stop(string, RunStopReason)"/>.
    /// First-trip latches on <see cref="ProcInfo.EnvironmentBlockerTripped"/>
    /// so a flurry of repeated stderr lines produces exactly one outcome.
    /// </summary>
    private void CheckEnvironmentBlocker(string jobKey, ProcInfo info, CliOutputLine rawLine)
    {
        if (info.EnvironmentBlockerTripped) return;
        AgentEnvironmentDetector.EnvironmentBlockerPattern? match;
        try { match = AgentEnvironmentDetector.Match(rawLine.Text); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Environment-blocker detector threw for {JobKey}", jobKey);
            return;
        }
        if (match == null) return;

        info.EnvironmentBlockerHitCount++;
        var threshold = match.ImmediateTerminate ? 1 : AgentEnvironmentDetector.HitThreshold;
        if (info.EnvironmentBlockerHitCount < threshold) return;

        info.EnvironmentBlockerTripped = true;
        var diagnosis = AgentEnvironmentDetector.Diagnose(match, CliType);

        var synthetic = new CliOutputLine
        {
            Timestamp = DateTime.UtcNow,
            Stream = "system",
            Text = $"[environment-blocker] {diagnosis}"
        };
        info.OutputBuffer.Add(synthetic);
        try
        {
            if (!info.OutputLog.Append(synthetic))
                _logger.LogWarning("Failed to persist environment-blocker marker for {JobKey}", jobKey);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Persisting environment-blocker marker failed for {JobKey}", jobKey); }
        try { OnOutput?.Invoke(jobKey, synthetic); } catch { }

        _logger.LogWarning(
            "Environment blocker '{Pattern}' detected for {Cli} job {JobKey} after {Hits} hit(s); terminating run",
            match.Id, CliType, jobKey, info.EnvironmentBlockerHitCount);

        try { Stop(jobKey, RunStopReason.EnvironmentBlocker); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stop after environment-blocker failed for {JobKey}", jobKey); }
    }

    private async Task ReadStreamAsync(string jobKey, StreamReader reader, string stream, ProcInfo info, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var rawLine = new CliOutputLine
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = stream,
                    Text = line
                };

                // Persist the raw line to the on-disk log unconditionally so
                // we never lose the source-of-truth bytes from the CLI — the
                // store flushes to disk per line so a backend crash here can
                // lose at most an in-flight write, never an acknowledged one.
                // The visible buffer + event stream get the transformed lines.
                if (!info.OutputLog.Append(rawLine))
                    _logger.LogWarning("Failed to persist CLI output line for {JobId}", jobKey);

                // Watchdog silence-clock reset: any real stdout/stderr line
                // counts as activity. Synthetic taskboard / orchestrator /
                // watchdog lines arrive via different paths (Append on the
                // OutputBuffer, not via this read loop) and therefore do not
                // reset the clock.
                info.LastStreamedAt = DateTime.UtcNow;

                // Pre-emptive environment-blocker check: a recognised
                // sandbox / OS-permission error means the agent cannot
                // self-recover. Trip a synthetic marker line + Stop()
                // immediately so the run finalizes with the correct
                // typed outcome instead of consuming the silence budget
                // while the agent retries against the same wall.
                CheckEnvironmentBlocker(jobKey, info, rawLine);

                // ADR-0013: typed events. Map this raw line to zero or more
                // CliRunEvent instances and raise them on OnRunEvent. The
                // mapping runs alongside (not instead of) TransformReadLine
                // so the legacy activity-log marker stream stays intact.
                IEnumerable<CliRunEvent>? runEvents = null;
                try { runEvents = MapLineToRunEvents(jobKey, rawLine); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MapLineToRunEvents threw for {JobId}; skipping typed events for this line", jobKey);
                }
                if (runEvents != null)
                {
                    foreach (var evt in runEvents) RaiseRunEvent(jobKey, evt);
                }

                IEnumerable<CliOutputLine> transformed;
                try { transformed = TransformReadLine(rawLine); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TransformReadLine threw for {JobId}; falling back to raw", jobKey);
                    transformed = new[] { rawLine };
                }

                foreach (var outputLine in transformed)
                {
                    info.OutputBuffer.Add(outputLine);
                    while (info.OutputBuffer.Count > 5000) info.OutputBuffer.RemoveAt(0);

                    try { OnOutputLine(info, outputLine); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OnOutputLine subclass hook threw for {JobId}", jobKey); }

                    // Event subscribers are out of our control (SignalR hub, etc).
                    // A throw here used to kill the whole API process — guard it.
                    try { OnOutput?.Invoke(jobKey, outputLine); }
                    catch (Exception ex) { _logger.LogWarning(ex, "OnOutput subscriber threw for {JobId}", jobKey); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading {Stream} for {Cli} job {JobId}", stream, CliType, jobKey);
        }
    }

    private async Task MonitorProcessAsync(string jobKey, Process process, ProcInfo info, CancellationToken ct)
    {
        try
        {
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { Stop(jobKey, RunStopReason.Cancelled); }

            // Drain the read loops before we write the synthetic "exited"
            // marker. Process.WaitForExitAsync returns as soon as the OS
            // notices the child is gone; the OS pipe still holds bytes the
            // CLI wrote just before exit. Without this wait, a Node child
            // that bursts 500 lines and exits leaves the runner with ~70
            // captured plus a misleading "CLI exited" marker on top - the
            // remaining 430 lines arrive after the marker (or get lost on
            // backend shutdown). 5s is the cap so a stuck read does not
            // pin exit; in practice the drain finishes in &lt; 100 ms.
            try
            {
                var drains = Task.WhenAll(
                    info.StdoutReadTask ?? Task.CompletedTask,
                    info.StderrReadTask ?? Task.CompletedTask);
                await drains.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("ReadStream drain timed out for {JobId}; some output may be missing", jobKey);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ReadStream drain threw for {JobId}", jobKey);
            }

            // Drop the active-job entry as soon as the process is known to be
            // gone, before any subscriber notifications. Keeps the reaper file
            // tight and avoids killing the next process that gets the same PID.
            try { RemoveActiveJob(jobKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear active-job entry for {JobKey}", jobKey); }

            var duration = (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }
            var status = RunStatusClassifier.Classify(exitCode, info.StopReason);

            var finalExecution = info.Execution with
            {
                Status = status,
                ExitCode = exitCode,
                DurationSeconds = duration
            };
            info.Execution = finalExecution;

            // Synthetic exit line so the Activity log shows a clear close even
            // when the CLI emitted nothing on stdout/stderr (rate-limit hangs,
            // immediate auth failures, etc).
            var exitLine = new CliOutputLine
            {
                Timestamp = DateTime.UtcNow,
                Stream = "system",
                Text = $"[taskboard] {CliType} CLI exited: status={status}, exitCode={exitCode?.ToString() ?? "?"}, duration={duration:F1}s"
            };
            info.OutputBuffer.Add(exitLine);
            info.OutputLog.Append(exitLine);
            try { OnOutput?.Invoke(jobKey, exitLine); } catch { }

            // ADR-0013 typed events: emit ProcessExited (or Killed if the
            // exit was a deliberate Stop) so the runner's phase tracker
            // sees the terminal state on the typed channel too.
            if (info.StopReason != RunStopReason.None)
                RaiseRunEvent(jobKey, new CliRunEvent.Killed(info.StopReason.ToString()) { JobKey = jobKey });
            else
                RaiseRunEvent(jobKey, new CliRunEvent.ProcessExited(exitCode, status, duration) { JobKey = jobKey });

            try { OnFinished?.Invoke(jobKey, finalExecution); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnFinished subscriber threw for {JobId}", jobKey); }

            _logger.LogInformation("{Cli} finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s",
                CliType, jobKey, exitCode, duration);

            _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(_ =>
            {
                if (_processes.TryRemove(jobKey, out var removed))
                    removed.OutputLog.Dispose();
            });
        }
        catch (Exception ex)
        {
            // Fire-and-forget tasks must never throw to the unobserved-task
            // handler — that's been crashing the host on subscriber exceptions.
            _logger.LogError(ex, "MonitorProcessAsync crashed for {JobId}", jobKey);
        }
    }

    // ── Output log persistence ───────────────────────────────────────────

    /// <summary>
    /// Resolve the per-job runtime JSONL path. Public so the runner can
    /// recover the Activity Log from disk after a backend restart, when no
    /// <see cref="ProcInfo"/> exists in memory anymore.
    /// </summary>
    public string GetOutputLogPath(string jobKey)
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime", "cli-output")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "cli-output");
        Directory.CreateDirectory(baseDir);
        var safe = SanitizeForFile($"{CliType}-{jobKey}");
        return Path.Combine(baseDir, $"{safe}.jsonl");
    }

    private static string SanitizeForFile(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length > 180 ? name[^180..] : name;
    }

    // ── Active-job tracking + orphan reaper ──────────────────────────────
    //
    // Why this exists: a CLI run is a child process of the backend. On a
    // backend crash / `dotnet watch` rebuild / IDE stop, that child can
    // outlive its parent — silently editing files, calling APIs, burning
    // quota with no UI to watch it. The next backend start therefore reaps:
    // reads the persisted PIDs, kills any that are still alive (with a
    // PID-recycling check via process name + start time), and clears the
    // file. Cheaper and less risky than re-attaching, which would need a
    // working stdout pipe we can't get back.

    private record ActiveJob
    {
        public string JobKey { get; init; } = "";
        public string JobId { get; init; } = "";
        public int ProcessId { get; init; }
        public string? ProcessName { get; init; }
        public DateTime? ProcessStartTimeUtc { get; init; }
        public DateTime StartedAt { get; init; }
    }

    private readonly object _activeJobsLock = new();
    private static readonly JsonSerializerOptions ActiveJobsJsonOpts = new() { WriteIndented = true };

    private string GetActiveJobsPath()
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, $"active-jobs-{CliType}.json");
    }

    private List<ActiveJob> ReadActiveJobs()
    {
        var path = GetActiveJobsPath();
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ActiveJob>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read active-jobs file at {Path}", path);
            return [];
        }
    }

    private void WriteActiveJobs(List<ActiveJob> list)
    {
        try
        {
            File.WriteAllText(GetActiveJobsPath(), JsonSerializer.Serialize(list, ActiveJobsJsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write active-jobs file");
        }
    }

    private void UpsertActiveJob(ActiveJob entry)
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            list.RemoveAll(e => e.JobKey == entry.JobKey);
            list.Add(entry);
            WriteActiveJobs(list);
        }
    }

    private void RemoveActiveJob(string jobKey)
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            var removed = list.RemoveAll(e => e.JobKey == jobKey);
            if (removed > 0) WriteActiveJobs(list);
        }
    }

    private static string? SafeProcessName(Process p)
    {
        try { return p.ProcessName; } catch { return null; }
    }

    private static DateTime? SafeProcessStartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    /// <summary>
    /// Reads the persisted active-jobs file and kills any process that is
    /// still alive (orphan from a previous backend run). PID recycling is
    /// guarded by matching <see cref="Process.ProcessName"/> and
    /// <see cref="Process.StartTime"/> against the persisted values — a
    /// 5-second tolerance accounts for clock skew between the recorded UTC
    /// time and what Windows reports back. The file is always cleared at
    /// the end so a half-clean run never leaves partial state behind.
    /// </summary>
    protected void ReapOrphans()
    {
        lock (_activeJobsLock)
        {
            var list = ReadActiveJobs();
            if (list.Count == 0) return;

            foreach (var entry in list)
            {
                Process? proc = null;
                try { proc = Process.GetProcessById(entry.ProcessId); }
                catch (ArgumentException) { proc = null; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetProcessById failed for {Pid} ({Cli})", entry.ProcessId, CliType);
                    continue;
                }

                if (proc == null) continue;

                try
                {
                    if (proc.HasExited) continue;

                    // PID-recycling guard: if the running process clearly isn't
                    // the one we recorded, leave it alone.
                    if (!string.IsNullOrEmpty(entry.ProcessName))
                    {
                        var liveName = SafeProcessName(proc);
                        if (!string.IsNullOrEmpty(liveName) &&
                            !string.Equals(liveName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("Skipping reap of PID {Pid}: name '{Live}' != recorded '{Recorded}'",
                                entry.ProcessId, liveName, entry.ProcessName);
                            continue;
                        }
                    }
                    if (entry.ProcessStartTimeUtc.HasValue)
                    {
                        var liveStart = SafeProcessStartTime(proc);
                        if (liveStart.HasValue &&
                            Math.Abs((liveStart.Value - entry.ProcessStartTimeUtc.Value).TotalSeconds) > 5)
                        {
                            _logger.LogDebug("Skipping reap of PID {Pid}: start time mismatch ({Live} vs {Recorded})",
                                entry.ProcessId, liveStart, entry.ProcessStartTimeUtc);
                            continue;
                        }
                    }

                    proc.Kill(entireProcessTree: true);
                    _logger.LogWarning("Reaped orphan {Cli} CLI for job {Job} (PID {Pid}) left over from a previous backend run",
                        CliType, entry.JobId, entry.ProcessId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reap PID {Pid} ({Cli})", entry.ProcessId, CliType);
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }

            // Always wipe the file: any process that legitimately survives
            // (PID-recycling skip) was not ours to track anyway. New runs
            // will repopulate via UpsertActiveJob.
            WriteActiveJobs([]);
        }
    }

    /// <summary>Per-process bookkeeping shared with subclasses.</summary>
    protected sealed class ProcInfo
    {
        public Process Process { get; }
        public CliExecution Execution { get; set; }
        public string WorkingDirectory { get; }
        public List<CliOutputLine> OutputBuffer { get; } = [];
        public SessionUsage? LastUsage { get; set; }
        public string? OutputLogPath { get; init; }
        public CliOutputLogStore OutputLog { get; init; } = null!;
        public string? SessionName { get; set; }
        /// <summary>For Codex: the UUID extracted from the first <c>session_meta</c> JSON line.</summary>
        public string? CapturedSessionId { get; set; }

        /// <summary>
        /// Latest <see cref="ParsedTurnUsage"/> captured from a CLI "turn
        /// finished" frame (Codex: <c>turn.completed</c>; Claude: <c>result</c>).
        /// Set by the per-CLI <see cref="OnOutputLine"/> hook when the adapter
        /// recognises the frame; consumed by the runner to mirror the usage
        /// onto the agent message bus as a <c>kind:token-usage</c> message so
        /// the workspace timeline and the token aggregation cache see the
        /// coding agent's own per-turn spend.
        /// </summary>
        public ParsedTurnUsage? LastParsedUsage { get; set; }

        /// <summary>UTC timestamp the most recent <see cref="LastParsedUsage"/> frame was observed.</summary>
        public DateTime? LastParsedUsageAt { get; set; }

        /// <summary>For Claude: the latest <c>rate_limit_event</c> frame parsed
        /// from the stream-json output. Null until the first event arrives.</summary>
        public ClaudeRateLimitSnapshot? LastRateLimit { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent <b>real</b> streamed line - lines
        /// that came off the CLI's stdout/stderr, not synthetic taskboard /
        /// orchestrator / watchdog markers we emitted ourselves. Drives
        /// <see cref="Watchdog"/> silence-clock decisions. Initialized to
        /// <see cref="CliExecution.StartedAt"/> on spawn so the watchdog
        /// starts measuring from run start, not from the synthetic Started
        /// line we add immediately afterward.
        /// </summary>
        public DateTime LastStreamedAt { get; set; }

        /// <summary>
        /// Last <see cref="WatchdogState"/> the runner observed for this
        /// process. Used by the runner's per-tick announcer so identical
        /// states do not produce duplicate chat meta lines.
        /// </summary>
        public WatchdogState LastWatchdogState { get; set; } = WatchdogState.Healthy;

        /// <summary>
        /// Set by <see cref="Stop(string, RunStopReason)"/> immediately
        /// before the kill so <see cref="MonitorProcessAsync"/> can
        /// classify the resulting exit as a deliberate stop instead of a
        /// crash. Stays at <see cref="RunStopReason.None"/> for natural
        /// exits - that is the signal the classifier uses to fall back to
        /// the exit-code-based completed/failed mapping.
        /// </summary>
        public RunStopReason StopReason { get; set; } = RunStopReason.None;

        /// <summary>
        /// Set when the child was spawned via PTY: <see cref="Process.Kill"/>
        /// can race the PTY teardown and leave the agent.exe orphaned. Custom
        /// hook lets the spawner do its own teardown (PtyConnection.Kill()
        /// plus the underlying child's process tree).
        /// </summary>
        public Action<RunStopReason>? KillOverride { get; init; }

        /// <summary>
        /// The stdin stream captured at spawn time (PTY writer for PTY-based
        /// subclasses; the Process's stdin BaseStream for the default path).
        /// <see cref="SendInput"/> writes here so PTY subclasses don't bypass
        /// the pseudo-terminal. Null on legacy ProcInfo construction (the
        /// constructor accepts a bare Process for backward compatibility);
        /// callers fall back to <c>Process.StandardInput</c> in that case.
        /// </summary>
        public Stream? ChildStdin { get; init; }

        /// <summary>
        /// Number of <see cref="AgentEnvironmentDetector"/> hits observed
        /// in this run's raw output. The base class read loop increments
        /// this per matching line; the threshold check decides whether
        /// to trip the blocker.
        /// </summary>
        public int EnvironmentBlockerHitCount { get; set; }

        /// <summary>
        /// Latch set once the run has been killed for an environment
        /// blocker. Stops subsequent matching lines from re-tripping the
        /// detector or producing duplicate synthetic markers.
        /// </summary>
        public bool EnvironmentBlockerTripped { get; set; }

        /// <summary>
        /// Read-loop tasks captured at spawn time. <see cref="MonitorProcessAsync"/>
        /// awaits them (with a short timeout) before writing the synthetic
        /// "CLI exited" line, so bursts of stdout that finish just before
        /// process exit reach <see cref="OutputBuffer"/> in their natural
        /// order rather than after the exit marker.
        /// </summary>
        public Task? StdoutReadTask { get; set; }
        public Task? StderrReadTask { get; set; }

        public ProcInfo(Process process, CliExecution execution, string workingDirectory)
        {
            Process = process;
            Execution = execution;
            WorkingDirectory = workingDirectory;
        }
    }
}
