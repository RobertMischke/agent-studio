using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;

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
    protected static string ResolveExecutable(string nameOrPath)
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

    /// <summary>Subclass hook: try to extract session metadata from a fresh output line.</summary>
    protected virtual void OnOutputLine(ProcInfo info, CliOutputLine line) { }

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
        psi.RedirectStandardInput  = true;
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

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
            // Some CLIs (notably Claude Code) read stdin even when the prompt is
            // passed via -p, and emit a 3-second "no stdin data received" warning
            // before continuing. We have no input to send, so signal EOF up front
            // to skip the warning and the wasted wall time.
            try { process.StandardInput.Close(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Cli} CLI for job {JobId}", CliType, jobId);
            return (null, $"Failed to start {CliType} CLI: {ex.Message}");
        }

        var execution = new CliExecution
        {
            JobId = jobId,
            JobKey = jobKey,
            ProcessId = process.Id,
            StartedAt = DateTime.UtcNow,
            Status = "running",
            Model = string.IsNullOrWhiteSpace(model) ? null : model
        };

        var info = new ProcInfo(process, execution, workingDirectory)
        {
            OutputLogPath = GetOutputLogPath(jobKey),
            SessionName = sessionName
        };
        ResetOutputLog(info.OutputLogPath);
        _processes[jobKey] = info;

        OnStarted?.Invoke(jobKey, execution);
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
        try { AppendOutputLine(info.OutputLogPath, startedLine); } catch { }
        try { OnOutput?.Invoke(jobKey, startedLine); } catch { }

        _ = ReadStreamAsync(jobKey, process.StandardOutput, "stdout", info, ct);
        _ = ReadStreamAsync(jobKey, process.StandardError,  "stderr", info, ct);
        _ = MonitorProcessAsync(jobKey, process, info, ct);
        _ = HeartbeatAsync(jobKey, info, ct);

        return (execution, null);
    }

    public bool Stop(string jobKey)
    {
        if (!_processes.TryGetValue(jobKey, out var info)) return false;
        try
        {
            if (!info.Process.HasExited)
            {
                info.Process.Kill(entireProcessTree: true);
                _logger.LogInformation("Killed {Cli} process for job {JobId}", CliType, jobKey);
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
            info.Process.StandardInput.WriteLine(input);
            return true;
        }
        catch { return false; }
    }

    public List<CliOutputLine> GetOutput(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.OutputBuffer.ToList() : [];

    public CliExecution? GetExecution(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.Execution : null;

    public SessionUsage? GetLastUsage(string jobKey) =>
        _processes.TryGetValue(jobKey, out var info) ? info.LastUsage : null;

    public bool IsRunningForProject(string rootPath) =>
        _processes.Values.Any(p => p.WorkingDirectory == rootPath && !p.Process.HasExited);

    /// <summary>
    /// Default implementation: nothing to reattach. Subclasses can override if they
    /// persist process info between restarts (Copilot does this in its own service).
    /// </summary>
    public virtual void ReattachOnStartup() { }

    private async Task ReadStreamAsync(string jobKey, StreamReader reader, string stream, ProcInfo info, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var outputLine = new CliOutputLine
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = stream,
                    Text = line
                };

                info.OutputBuffer.Add(outputLine);
                while (info.OutputBuffer.Count > 5000) info.OutputBuffer.RemoveAt(0);

                try { AppendOutputLine(info.OutputLogPath, outputLine); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to append output line for {JobId}", jobKey); }

                try { OnOutputLine(info, outputLine); }
                catch (Exception ex) { _logger.LogWarning(ex, "OnOutputLine subclass hook threw for {JobId}", jobKey); }

                // Event subscribers are out of our control (SignalR hub, etc).
                // A throw here used to kill the whole API process — guard it.
                try { OnOutput?.Invoke(jobKey, outputLine); }
                catch (Exception ex) { _logger.LogWarning(ex, "OnOutput subscriber threw for {JobId}", jobKey); }
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
            catch (OperationCanceledException) { Stop(jobKey); }

            var duration = (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }
            var status = exitCode == 0 ? "completed" : "failed";

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
            try { AppendOutputLine(info.OutputLogPath, exitLine); } catch { }
            try { OnOutput?.Invoke(jobKey, exitLine); } catch { }

            try { OnFinished?.Invoke(jobKey, finalExecution); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnFinished subscriber threw for {JobId}", jobKey); }

            _logger.LogInformation("{Cli} finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s",
                CliType, jobKey, exitCode, duration);

            _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(_ =>
            {
                _processes.TryRemove(jobKey, out ProcInfo? _removed);
            });
        }
        catch (Exception ex)
        {
            // Fire-and-forget tasks must never throw to the unobserved-task
            // handler — that's been crashing the host on subscriber exceptions.
            _logger.LogError(ex, "MonitorProcessAsync crashed for {JobId}", jobKey);
        }
    }

    /// <summary>
    /// Emits a "still running, no output for Ns" line every 30 s while the
    /// CLI is silent, so the Activity log shows progress feedback even for
    /// CLIs that batch their output until the very end (Claude `-p` mode is
    /// the typical offender). Stops as soon as the process exits.
    /// </summary>
    private async Task HeartbeatAsync(string jobKey, ProcInfo info, CancellationToken ct)
    {
        const int IntervalMs = 30_000;
        try
        {
            var startedAt = info.Execution.StartedAt;
            int lastSeenLineCount = info.OutputBuffer.Count;
            while (!ct.IsCancellationRequested && !info.Process.HasExited)
            {
                await Task.Delay(IntervalMs, ct);
                if (info.Process.HasExited) break;
                var currentCount = info.OutputBuffer.Count;
                if (currentCount > lastSeenLineCount)
                {
                    // Real output arrived — silence broken, no heartbeat needed.
                    lastSeenLineCount = currentCount;
                    continue;
                }
                var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
                var hb = new CliOutputLine
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = "system",
                    Text = $"[taskboard] still running, no CLI output yet ({elapsed:F0}s elapsed)"
                };
                info.OutputBuffer.Add(hb);
                try { AppendOutputLine(info.OutputLogPath, hb); } catch { }
                try { OnOutput?.Invoke(jobKey, hb); } catch { }
                lastSeenLineCount = info.OutputBuffer.Count;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat task crashed for {JobId}", jobKey);
        }
    }

    // ── Output log persistence ───────────────────────────────────────────

    private string GetOutputLogPath(string jobKey)
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

    private static readonly object _outputLogLock = new();
    private static readonly JsonSerializerOptions OutputLineJsonOpts = new() { WriteIndented = false };

    private static void ResetOutputLog(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.WriteAllText(path, string.Empty); } catch { }
    }

    private static void AppendOutputLine(string? path, CliOutputLine line)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var json = JsonSerializer.Serialize(line, OutputLineJsonOpts);
            lock (_outputLogLock) File.AppendAllText(path, json + Environment.NewLine);
        }
        catch { }
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
        public string? SessionName { get; set; }
        /// <summary>For Codex: the UUID extracted from the first <c>session_meta</c> JSON line.</summary>
        public string? CapturedSessionId { get; set; }

        public ProcInfo(Process process, CliExecution execution, string workingDirectory)
        {
            Process = process;
            Execution = execution;
            WorkingDirectory = workingDirectory;
        }
    }
}
