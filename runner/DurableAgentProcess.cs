using System.Diagnostics;
using System.Text.Json;

namespace AgentRunner;

internal sealed record DetachedJobSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Prompt,
    string ResultsDirectory,
    int TimeoutSeconds);

internal sealed record DetachedJobLogLine(long Sequence, DateTime Timestamp, string Stream, string Text);

internal sealed record DetachedJobResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    DateTime CompletedAtUtc);

internal sealed class DetachedWorkerLostException(string message) : Exception(message);

/// <summary>
/// Starts an agent behind a tiny runner-owned worker process. systemd may stop
/// the daemon main PID while this worker continues. Output and the terminal
/// result live in files, so the replacement daemon can follow and finish the
/// same attempt without inheriting an anonymous pipe.
/// </summary>
internal sealed class DurableAgentProcess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    private DurableAgentProcess(string directory, int processId, DateTime processStartedAtUtc)
    {
        _directory = directory;
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
    }

    public int ProcessId { get; }
    public DateTime ProcessStartedAtUtc { get; }
    public string LogPath => Path.Combine(_directory, "output.jsonl");
    public string ResultPath => Path.Combine(_directory, "result.json");

    public static DurableAgentProcess Start(
        RunnerOptions options,
        string workerDirectory,
        string repoPath,
        string prompt,
        string resultsDirectory,
        IReadOnlyList<string>? argsOverride = null)
    {
        Directory.CreateDirectory(workerDirectory);
        var specPath = Path.Combine(workerDirectory, "spec.json");
        var spec = new DetachedJobSpec(
            options.CliBin,
            argsOverride ?? AgentCliProcess.SplitArgs(options.CliArgs),
            Path.GetFullPath(repoPath),
            prompt,
            Path.GetFullPath(resultsDirectory),
            options.RunTimeoutSeconds);
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, Json));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the runner executable for detached job launch.");
        var managedHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                          || executable.Contains("testhost", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = managedHost ? "dotnet" : executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        if (managedHost) start.ArgumentList.Add(typeof(DurableAgentProcess).Assembly.Location);
        start.ArgumentList.Add("--detached-worker");
        start.ArgumentList.Add(specPath);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the detached runner worker.");
        var started = process.StartTime.ToUniversalTime();
        var handle = new DurableAgentProcess(workerDirectory, process.Id, started);
        process.Dispose();
        return handle;
    }

    public static DurableAgentProcess Attach(PersistedRunnerSlot slot)
        => new(
            slot.WorkerDirectory,
            slot.ProcessId ?? -1,
            slot.ProcessStartedAtUtc ?? DateTime.MinValue);

    public static bool HasCompleted(PersistedRunnerSlot slot)
        => File.Exists(Path.Combine(slot.WorkerDirectory, "result.json"));

    public static bool VerifyLive(PersistedRunnerSlot slot, out string reason)
    {
        if (slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
        {
            reason = "no persisted process identity";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(slot.ProcessId.Value);
            if (process.HasExited)
            {
                reason = "process has exited without a durable result";
                return false;
            }
            if (Math.Abs((process.StartTime.ToUniversalTime() - slot.ProcessStartedAtUtc.Value).TotalSeconds) > 2)
            {
                reason = "PID was reused (process start time differs)";
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                var cwdLink = new DirectoryInfo($"/proc/{slot.ProcessId.Value}/cwd");
                var target = cwdLink.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (string.IsNullOrWhiteSpace(target) || !PathsEqual(target, slot.WorktreePath))
                {
                    reason = $"process cwd '{target ?? "unavailable"}' does not match worktree '{slot.WorktreePath}'";
                    return false;
                }
            }

            reason = "live process and worktree match";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            reason = $"process verification failed: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<DetachedJobLogLine> ReadAfter(long sequence)
    {
        if (!File.Exists(LogPath)) return [];
        var lines = new List<DetachedJobLogLine>();
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            try
            {
                var line = JsonSerializer.Deserialize<DetachedJobLogLine>(raw, Json);
                if (line is not null && line.Sequence > sequence) lines.Add(line);
            }
            catch (JsonException)
            {
                // The worker may currently be appending this final line. It will
                // be complete and parseable on the next poll.
            }
        }
        return lines.OrderBy(x => x.Sequence).ToList();
    }

    public DetachedJobResult? ReadResult()
    {
        if (!File.Exists(ResultPath)) return null;
        try { return JsonSerializer.Deserialize<DetachedJobResult>(File.ReadAllText(ResultPath), Json); }
        catch (JsonException) { return null; } // atomic rename normally makes this unreachable
    }

    public void Kill()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* lease loss/cancellation is already the authoritative outcome */ }
    }

    public static async Task<int> RunWorkerAsync(string specPath)
    {
        var spec = JsonSerializer.Deserialize<DetachedJobSpec>(await File.ReadAllTextAsync(specPath), Json)
            ?? throw new InvalidDataException($"Detached job spec is empty: {specPath}");
        var directory = Path.GetDirectoryName(specPath)!;
        var logPath = Path.Combine(directory, "output.jsonl");
        var resultPath = Path.Combine(directory, "result.json");
        Directory.CreateDirectory(spec.ResultsDirectory);
        long sequence = 0;
        var logGate = new object();
        using var logStream = new FileStream(
            logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var logWriter = new StreamWriter(logStream) { AutoFlush = true };

        void Append(string stream, string text)
        {
            var entry = new DetachedJobLogLine(Interlocked.Increment(ref sequence), DateTime.UtcNow, stream, text);
            var json = JsonSerializer.Serialize(entry, Json);
            lock (logGate) logWriter.WriteLine(json);
        }

        ProcessResult processResult;
        var timedOut = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        try
        {
            processResult = await ProcessRunner.RunAsync(
                spec.FileName,
                spec.Arguments,
                spec.WorkingDirectory,
                spec.Prompt,
                line => Append("stdout", line),
                line => Append("stderr", line),
                new Dictionary<string, string?> { ["JOB_RESULTS_DIR"] = spec.ResultsDirectory },
                clearEnvironment: false,
                ct: timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
            Append("system", $"[runner] run exceeded {spec.TimeoutSeconds}s timeout");
            processResult = new ProcessResult(124, string.Empty, "Runner timeout");
        }
        catch (Exception ex)
        {
            Append("system", $"[runner] detached worker failed: {ex.Message}");
            processResult = new ProcessResult(125, string.Empty, ex.ToString());
        }

        var result = new DetachedJobResult(
            processResult.ExitCode,
            processResult.StdOut,
            processResult.StdErr,
            timedOut,
            DateTime.UtcNow);
        var temp = resultPath + $".{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result, Json));
        File.Move(temp, resultPath, overwrite: true);
        return processResult.ExitCode;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
