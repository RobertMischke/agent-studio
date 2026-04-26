using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class CopilotCliService
{
    private static readonly string[] WindowsShellFallbackInstructions =
    [
        "Wenn Shell-Kommandos notwendig sind, verwende auf Windows keine pwsh.exe-abhaengigen Befehle.",
        "Bevorzuge cmd.exe, normale Windows-Batch-Syntax oder direkte Node/npm-Kommandos.",
        "Falls eine Plan-Datei erstellt werden muss, nutze eine Methode ohne PowerShell-spezifische Syntax wie @'... '@, Out-File oder Set-Content.",
        "Dokumentiere kurz, welche Alternative verwendet wurde.",
        "Wenn ein Build in der aktuellen Umgebung nicht moeglich ist, nenne den konkreten Grund und fahre mit statischer Pruefung fort."
    ];

    private readonly ILogger<CopilotCliService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, CliProcessInfo> _processes = new();
    private string? _cliPathOverride;
    private string? _githubTokenOverride;

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;

    public CopilotCliService(ILogger<CopilotCliService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public string GetCliPath() => _cliPathOverride ?? _configuration["CliPath"] ?? "copilot";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("CLI path set to: {Path}", GetCliPath());
    }

    public string? GetGitHubToken() => _githubTokenOverride ?? _configuration["GitHubToken"];

    public void SetGitHubToken(string? token)
    {
        _githubTokenOverride = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _logger.LogInformation("GitHub token {Action}", _githubTokenOverride != null ? "set" : "cleared");
    }

    public bool HasGitHubToken() => !string.IsNullOrWhiteSpace(GetGitHubToken()) || TryGetGhAuthToken() != null;

    public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
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

    public bool IsAvailable()
    {
        var (available, _, _) = TestCliPath();
        return available;
    }

    public async Task<(CliExecution? Execution, string? Error)> StartAsync(string jobId, string jobKey, string prompt, string workingDirectory, CancellationToken ct = default)
    {
        if (_processes.TryGetValue(jobKey, out var existing))
        {
            if (!existing.Process.HasExited)
            {
                _logger.LogWarning("CLI process already running for job {JobId}", jobKey);
                return (null, $"CLI process already running for job '{jobId}'");
            }
            // Previous process finished/failed — clean up and allow restart
            _logger.LogInformation("Clearing stale CLI entry for job {JobId} (exit={ExitCode})", jobKey, existing.Process.ExitCode);
            _processes.TryRemove(jobKey, out _);
        }

        var promptArg = string.Join(" ",
        [
            $"Lies @.orchestrator/jobs/3-progress/{jobId}/prompt.md und fuehre den Task aus.",
            $"Schreibe deinen Completion-Report in .orchestrator/jobs/3-progress/{jobId}/status.md.",
            .. WindowsShellFallbackInstructions
        ]);

        var psi = new ProcessStartInfo
        {
            FileName = GetCliPath(),
            Arguments = $"-p \"{EscapeArg(promptArg)}\" --autopilot --yolo",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var token = GetGitHubToken() ?? TryGetGhAuthToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            psi.Environment["GITHUB_TOKEN"] = token;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Copilot CLI for job {JobId}", jobId);
            return (null, $"Failed to start CLI process: {ex.Message}");
        }

        var execution = new CliExecution
        {
            JobId = jobId,
            JobKey = jobKey,
            ProcessId = process.Id,
            StartedAt = DateTime.UtcNow,
            Status = "running"
        };

        var info = new CliProcessInfo(process, execution, workingDirectory);
        _processes[jobKey] = info;

        UpsertPersisted(new PersistedExecution
        {
            JobId = jobId,
            JobKey = jobKey,
            WorkingDirectory = workingDirectory,
            ProcessId = process.Id,
            StartedAt = execution.StartedAt,
            ProcessStartTime = SafeProcessStartTime(process),
            Status = "running"
        });

        OnStarted?.Invoke(jobKey, execution);
        _logger.LogInformation("Started Copilot CLI for job {JobId} (PID {Pid}) in {Cwd}", jobId, process.Id, workingDirectory);

        // Start reading stdout/stderr in background
        _ = ReadStreamAsync(jobKey, process.StandardOutput, "stdout", info, ct);
        _ = ReadStreamAsync(jobKey, process.StandardError, "stderr", info, ct);

        // Monitor process exit in background
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
                _logger.LogInformation("Killed CLI process for job {JobId}", jobKey);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill CLI process for job {JobId}", jobKey);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send input to CLI process for job {JobId}", jobKey);
            return false;
        }
    }

    public List<CliOutputLine> GetOutput(string jobKey)
    {
        return _processes.TryGetValue(jobKey, out var info)
            ? info.OutputBuffer.ToList()
            : [];
    }

    public CliExecution? GetExecution(string jobKey)
    {
        return _processes.TryGetValue(jobKey, out var info) ? info.Execution : null;
    }

    public bool IsRunningForProject(string rootPath)
    {
        return _processes.Values.Any(p => p.WorkingDirectory == rootPath && !p.Process.HasExited);
    }

    private async Task ReadStreamAsync(string jobKey, System.IO.StreamReader reader, string stream, CliProcessInfo info, CancellationToken ct)
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

                // Trim buffer if too large (keep last 5000 lines)
                while (info.OutputBuffer.Count > 5000)
                    info.OutputBuffer.RemoveAt(0);

                OnOutput?.Invoke(jobKey, outputLine);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading {Stream} for job {JobId}", stream, jobKey);
        }
    }

    private async Task MonitorProcessAsync(string jobKey, Process process, CliProcessInfo info, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Stop(jobKey);
        }

        var duration = (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { /* reattached process may deny ExitCode access */ }
        var status = exitCode == 0 ? "completed" : "failed";

        var finalExecution = info.Execution with
        {
            Status = status,
            ExitCode = exitCode,
            DurationSeconds = duration
        };
        info.Execution = finalExecution;

        UpsertPersisted(new PersistedExecution
        {
            JobId = info.Execution.JobId,
            JobKey = jobKey,
            WorkingDirectory = info.WorkingDirectory,
            ProcessId = process.Id,
            StartedAt = info.Execution.StartedAt,
            FinishedAt = DateTime.UtcNow,
            Status = status,
            ExitCode = exitCode
        });

        // Write output log to job folder
        WriteOutputLog(jobKey, info);

        OnFinished?.Invoke(jobKey, finalExecution);
        _logger.LogInformation("CLI finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s", jobKey, process.ExitCode, duration);

        // Keep in _processes for output retrieval; cleanup after a delay
        _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(t =>
        {
            _processes.TryRemove(jobKey, out CliProcessInfo? _removed);
        });
    }

    private void WriteOutputLog(string jobKey, CliProcessInfo info)
    {
        try
        {
            // Find job folder — look through all watch paths
            foreach (var proc in _processes.Values.Where(p => p.Execution.JobKey == jobKey))
            {
                // The job folder is at {watchPath}/3-progress/{jobId} relative to root
                // But we don't have direct access to the watch path here, so we write via the info's working directory
                // The caller (TaskRunnerService) handles the log path
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write output log for job {JobId}", jobKey);
        }
    }

    private string? TryGetGhAuthToken()
    {
        try
        {
            // Try common locations for gh CLI
            var candidates = new[]
            {
                "gh",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gh-cli", "bin", "gh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe")
            };

            foreach (var ghPath in candidates)
            {
                try
                {
                    using var proc = new Process();
                    proc.StartInfo = new ProcessStartInfo
                    {
                        FileName = ghPath,
                        Arguments = "auth token",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    proc.Start();
                    var token = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(token))
                    {
                        _logger.LogInformation("Resolved GitHub token via gh CLI at {Path}", ghPath);
                        return token;
                    }
                }
                catch { /* try next candidate */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get token from gh CLI");
        }
        return null;
    }

    private static string EscapeArg(string arg) => arg.Replace("\"", "\\\"");

    // ─────────────────────────────────────────────────────────────────────
    // Persistence + Reattach
    // ─────────────────────────────────────────────────────────────────────

    private record PersistedExecution
    {
        public string JobId { get; init; } = "";
        public string JobKey { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public int ProcessId { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? ProcessStartTime { get; init; }
        public DateTime? FinishedAt { get; init; }
        public string Status { get; init; } = "running";
        public int? ExitCode { get; init; }
    }

    private readonly object _persistLock = new();
    private static readonly JsonSerializerOptions PersistJsonOpts = new() { WriteIndented = true };

    private string GetPersistencePath()
    {
        var taskRepo = _configuration["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, "executions.json");
    }

    private List<PersistedExecution> ReadPersistedExecutions()
    {
        var path = GetPersistencePath();
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PersistedExecution>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read persisted executions at {Path}", path);
            return [];
        }
    }

    private void WritePersistedExecutions(List<PersistedExecution> list)
    {
        try
        {
            File.WriteAllText(GetPersistencePath(), JsonSerializer.Serialize(list, PersistJsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write persisted executions");
        }
    }

    private void UpsertPersisted(PersistedExecution entry)
    {
        lock (_persistLock)
        {
            var list = ReadPersistedExecutions();
            list.RemoveAll(e => e.JobKey == entry.JobKey);
            list.Add(entry);
            WritePersistedExecutions(list);
        }
    }

    private static DateTime? SafeProcessStartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    /// <summary>
    /// Inspects the persistence file and re-attaches to any CLI processes that are still alive.
    /// Stale entries (process gone) are marked as <c>crashed</c> so the UI can show the final state.
    /// Stdout/stderr streams of pre-existing processes can no longer be read — only Stop and exit
    /// monitoring are supported on reattached entries.
    /// </summary>
    public void ReattachOnStartup()
    {
        lock (_persistLock)
        {
            var list = ReadPersistedExecutions();
            var changed = false;

            foreach (var pe in list.ToList())
            {
                if (pe.Status != "running") continue;
                if (_processes.ContainsKey(pe.JobKey)) continue;

                Process? proc = null;
                try
                {
                    proc = Process.GetProcessById(pe.ProcessId);
                }
                catch (ArgumentException)
                {
                    proc = null;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetProcessById failed for {Pid}", pe.ProcessId);
                }

                if (proc == null || proc.HasExited)
                {
                    list.Remove(pe);
                    list.Add(pe with { Status = "crashed", FinishedAt = DateTime.UtcNow });
                    changed = true;
                    _logger.LogInformation("Marking job {Job} as crashed (PID {Pid} no longer alive)", pe.JobId, pe.ProcessId);
                    continue;
                }

                // Heuristic: if the recorded ProcessStartTime differs from the live one,
                // the PID has been recycled by an unrelated process — treat as crashed.
                if (pe.ProcessStartTime.HasValue)
                {
                    var liveStart = SafeProcessStartTime(proc);
                    if (liveStart.HasValue && Math.Abs((liveStart.Value - pe.ProcessStartTime.Value).TotalSeconds) > 5)
                    {
                        list.Remove(pe);
                        list.Add(pe with { Status = "crashed", FinishedAt = DateTime.UtcNow });
                        changed = true;
                        _logger.LogWarning("PID {Pid} reused — marking job {Job} as crashed", pe.ProcessId, pe.JobId);
                        continue;
                    }
                }

                var execution = new CliExecution
                {
                    JobId = pe.JobId,
                    JobKey = pe.JobKey,
                    ProcessId = pe.ProcessId,
                    StartedAt = pe.StartedAt,
                    Status = "running"
                };
                var info = new CliProcessInfo(proc, execution, pe.WorkingDirectory);
                info.OutputBuffer.Add(new CliOutputLine
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = "stdout",
                    Text = $"[reattached to running process PID {pe.ProcessId} — live output unavailable, only stop and exit monitoring supported]"
                });
                _processes[pe.JobKey] = info;

                try { proc.EnableRaisingEvents = true; } catch { /* best effort */ }
                _ = MonitorProcessAsync(pe.JobKey, proc, info, CancellationToken.None);

                OnStarted?.Invoke(pe.JobKey, execution);
                _logger.LogInformation("Reattached to running job {Job} (PID {Pid})", pe.JobId, pe.ProcessId);
            }

            if (changed) WritePersistedExecutions(list);
        }
    }

    private class CliProcessInfo
    {
        public Process Process { get; }
        public CliExecution Execution { get; set; }
        public string WorkingDirectory { get; }
        public List<CliOutputLine> OutputBuffer { get; } = [];

        public CliProcessInfo(Process process, CliExecution execution, string workingDirectory)
        {
            Process = process;
            Execution = execution;
            WorkingDirectory = workingDirectory;
        }
    }
}
