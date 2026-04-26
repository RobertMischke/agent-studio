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

    public virtual (bool Available, string? Version, string Path) TestCliPath(string? path = null)
    {
        var testPath = path?.Trim() ?? GetCliPath();
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
            var version = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return (proc.ExitCode == 0, version, testPath);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, testPath);
        }
    }

    public bool IsAvailable() => TestCliPath().Available;

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

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
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

        _ = ReadStreamAsync(jobKey, process.StandardOutput, "stdout", info, ct);
        _ = ReadStreamAsync(jobKey, process.StandardError,  "stderr", info, ct);
        _ = MonitorProcessAsync(jobKey, process, info, ct);

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

                AppendOutputLine(info.OutputLogPath, outputLine);
                OnOutputLine(info, outputLine);
                OnOutput?.Invoke(jobKey, outputLine);
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

        OnFinished?.Invoke(jobKey, finalExecution);
        _logger.LogInformation("{Cli} finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s",
            CliType, jobKey, exitCode, duration);

        _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(_ =>
        {
            _processes.TryRemove(jobKey, out ProcInfo? _removed);
        });
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
