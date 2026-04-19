using System.Collections.Concurrent;
using System.Diagnostics;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class CopilotCliService
{
    private readonly ILogger<CopilotCliService> _logger;
    private readonly ConcurrentDictionary<string, CliProcessInfo> _processes = new();

    public event Action<string, CliOutputLine>? OnOutput;
    public event Action<string, CliExecution>? OnStarted;
    public event Action<string, CliExecution>? OnFinished;

    public CopilotCliService(ILogger<CopilotCliService> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable()
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "copilot",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CliExecution?> StartAsync(string jobId, string prompt, string workingDirectory, CancellationToken ct = default)
    {
        if (_processes.ContainsKey(jobId))
        {
            _logger.LogWarning("CLI process already running for job {JobId}", jobId);
            return null;
        }

        var promptArg = $"Lies @.orchestrator/jobs/3-progress/{jobId}/prompt.md und führe den Task aus. Schreibe deinen Completion-Report in .orchestrator/jobs/3-progress/{jobId}/status.md";

        var psi = new ProcessStartInfo
        {
            FileName = "copilot",
            Arguments = $"-p \"{EscapeArg(promptArg)}\" --autopilot --yolo",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Copilot CLI for job {JobId}", jobId);
            return null;
        }

        var execution = new CliExecution
        {
            JobId = jobId,
            ProcessId = process.Id,
            StartedAt = DateTime.UtcNow,
            Status = "running"
        };

        var info = new CliProcessInfo(process, execution, workingDirectory);
        _processes[jobId] = info;

        OnStarted?.Invoke(jobId, execution);
        _logger.LogInformation("Started Copilot CLI for job {JobId} (PID {Pid}) in {Cwd}", jobId, process.Id, workingDirectory);

        // Start reading stdout/stderr in background
        _ = ReadStreamAsync(jobId, process.StandardOutput, "stdout", info, ct);
        _ = ReadStreamAsync(jobId, process.StandardError, "stderr", info, ct);

        // Monitor process exit in background
        _ = MonitorProcessAsync(jobId, process, info, ct);

        return execution;
    }

    public bool Stop(string jobId)
    {
        if (!_processes.TryGetValue(jobId, out var info)) return false;

        try
        {
            if (!info.Process.HasExited)
            {
                info.Process.Kill(entireProcessTree: true);
                _logger.LogInformation("Killed CLI process for job {JobId}", jobId);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill CLI process for job {JobId}", jobId);
            return false;
        }
    }

    public bool SendInput(string jobId, string input)
    {
        if (!_processes.TryGetValue(jobId, out var info)) return false;
        if (info.Process.HasExited) return false;

        try
        {
            info.Process.StandardInput.WriteLine(input);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send input to CLI process for job {JobId}", jobId);
            return false;
        }
    }

    public List<CliOutputLine> GetOutput(string jobId)
    {
        return _processes.TryGetValue(jobId, out var info)
            ? info.OutputBuffer.ToList()
            : [];
    }

    public CliExecution? GetExecution(string jobId)
    {
        return _processes.TryGetValue(jobId, out var info) ? info.Execution : null;
    }

    public bool IsRunningForProject(string rootPath)
    {
        return _processes.Values.Any(p => p.WorkingDirectory == rootPath && !p.Process.HasExited);
    }

    private async Task ReadStreamAsync(string jobId, System.IO.StreamReader reader, string stream, CliProcessInfo info, CancellationToken ct)
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

                OnOutput?.Invoke(jobId, outputLine);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading {Stream} for job {JobId}", stream, jobId);
        }
    }

    private async Task MonitorProcessAsync(string jobId, Process process, CliProcessInfo info, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Stop(jobId);
        }

        var duration = (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
        var status = process.ExitCode == 0 ? "completed" : "failed";

        var finalExecution = info.Execution with
        {
            Status = status,
            ExitCode = process.ExitCode,
            DurationSeconds = duration
        };
        info.Execution = finalExecution;

        // Write output log to job folder
        WriteOutputLog(jobId, info);

        OnFinished?.Invoke(jobId, finalExecution);
        _logger.LogInformation("CLI finished for job {JobId}: exit={ExitCode}, duration={Duration:F1}s", jobId, process.ExitCode, duration);

        // Keep in _processes for output retrieval; cleanup after a delay
        _ = Task.Delay(TimeSpan.FromMinutes(30), CancellationToken.None).ContinueWith(t =>
        {
            _processes.TryRemove(jobId, out CliProcessInfo? _removed);
        });
    }

    private void WriteOutputLog(string jobId, CliProcessInfo info)
    {
        try
        {
            // Find job folder — look through all watch paths
            foreach (var proc in _processes.Values.Where(p => p.Execution.JobId == jobId))
            {
                // The job folder is at {watchPath}/3-progress/{jobId} relative to root
                // But we don't have direct access to the watch path here, so we write via the info's working directory
                // The caller (TaskRunnerService) handles the log path
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write output log for job {JobId}", jobId);
        }
    }

    private static string EscapeArg(string arg) => arg.Replace("\"", "\\\"");

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
