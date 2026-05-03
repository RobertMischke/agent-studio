using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services;

/// <summary>
/// Builds the post-run <c>status.md</c> protocol by handing the tail of the CLI
/// output log to a one-shot Claude Haiku subprocess. Runs fire-and-forget after
/// each successful CLI completion. State is in-memory only - after a backend
/// restart, jobs whose summary was mid-flight fall back to <c>None|Ready</c>
/// based on whether <c>status.md</c> exists on disk.
/// </summary>
public sealed class SummaryGenerationService
{
    private const int MaxLogChars = 60_000;
    private const int HaikuTimeoutSeconds = 90;

    private readonly ILogger<SummaryGenerationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly ConcurrentDictionary<string, JobSummaryState> _states = new();

    public SummaryGenerationService(ILogger<SummaryGenerationService> logger, IConfiguration configuration)
        : this(logger, configuration, new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance))
    {
    }

    public SummaryGenerationService(
        ILogger<SummaryGenerationService> logger,
        IConfiguration configuration,
        RuntimePromptService prompts)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
    }

    public JobSummaryState? GetState(string jobKey)
        => _states.TryGetValue(jobKey, out var s) ? s : null;

    /// <summary>
    /// Pure inflight check used by <see cref="GenerateAsync"/> and exposed
    /// for tests. A job is considered "still generating" when its previous
    /// state is <see cref="JobSummaryStatus.Generating"/> AND the
    /// <see cref="JobSummaryState.StartedAt"/> is younger than the Haiku
    /// timeout. Older Generating entries are treated as stuck and
    /// overwritten so the user can recover via the regenerate button.
    /// </summary>
    public static bool IsInflight(JobSummaryState? prev, DateTime nowUtc, int timeoutSeconds)
    {
        if (prev is null) return false;
        if (prev.Status != JobSummaryStatus.Generating) return false;
        if (prev.StartedAt is null) return false;
        return (nowUtc - prev.StartedAt.Value).TotalSeconds < timeoutSeconds;
    }

    public async Task GenerateAsync(JobInfo info, CancellationToken ct = default)
    {
        var key = info.JobKey;

        // Inflight guard: if a previous GenerateAsync for the same job is
        // still inside its Haiku window, dropping this duplicate avoids
        // racing two subprocesses against the same status.md (manual
        // Regenerate clicked while the post-run auto-call is still in
        // flight, or the runner re-fires after a missed completion). The
        // outstanding call will publish either Ready or Failed when it
        // returns; the user-visible spinner stays where it was.
        if (_states.TryGetValue(key, out var prev) && IsInflight(prev, DateTime.UtcNow, HaikuTimeoutSeconds))
        {
            _logger.LogDebug("Skipping summary generation for {JobId}: prior call still in flight (started {StartedAt:o})",
                info.Id, prev.StartedAt);
            return;
        }

        _states[key] = new JobSummaryState
        {
            Status = JobSummaryStatus.Generating,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            var logPath = JobPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath))
            {
                Fail(key, "No CLI output to summarise yet. The task has not been run (logs/cli-output.log is missing). Start it once, then try again.");
                return;
            }

            var rawLog = await File.ReadAllTextAsync(logPath, ct);
            var truncated = TruncateTail(rawLog, MaxLogChars);
            var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol,
                new Dictionary<string, string?> { ["log"] = truncated });

            var (ok, summary, error) = await RunHaikuAsync(prompt, info.FolderPath, ct);
            if (!ok || string.IsNullOrWhiteSpace(summary))
            {
                Fail(key, error ?? "Empty Haiku response");
                return;
            }

            var target = Path.Combine(info.FolderPath, "status.md");
            WriteAllTextWithRetry(target, summary);

            _states[key] = new JobSummaryState
            {
                Status = JobSummaryStatus.Ready,
                StartedAt = _states[key].StartedAt,
                FinishedAt = DateTime.UtcNow,
                BytesWritten = summary.Length
            };
            _logger.LogInformation("Summary written for {JobId} ({Bytes} bytes)", info.Id, summary.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary generation failed for {JobId}", info.Id);
            Fail(key, ex.Message);
        }
    }

    private void Fail(string key, string error)
    {
        var prev = _states.TryGetValue(key, out var s) ? s : new JobSummaryState();
        _states[key] = prev with
        {
            Status = JobSummaryStatus.Failed,
            FinishedAt = DateTime.UtcNow,
            ErrorMessage = error
        };
    }

    private static string TruncateTail(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var tail = text[^maxChars..];
        return "[earlier output truncated]\n" + tail;
    }

    private async Task<(bool Ok, string? Summary, string? Error)> RunHaikuAsync(
        string prompt, string workingDirectory, CancellationToken ct)
    {
        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";
        var model = _configuration["ClaudeCli:SummaryModel"] ?? "claude-haiku-4-5";

        // Feed the prompt via stdin instead of a positional `-p <prompt>`
        // argument. The summary prompt embeds up to MaxLogChars (60 000) of
        // CLI log; that combined with `--model …` and the executable path
        // overruns Windows' 32 767-char CreateProcess command-line cap and
        // returns "The command line is too long." Stdin has no such limit
        // and Claude Code's `-p` mode reads the user message from stdin
        // when no positional prompt is provided.
        var psi = new ProcessStartInfo
        {
            FileName = CliExecutionServiceBase.ResolveExecutable(claudePath),
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, null, "Process.Start returned null");

            // Write the prompt up front, then close stdin so Claude can finalise
            // the request. WriteAsync is awaited so the OS pipe buffer can drain
            // before we move on to reading stdout.
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(HaikuTimeoutSeconds));
            await p.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (p.ExitCode != 0)
                return (false, null, $"claude exited {p.ExitCode}: {stderr.Trim()}");

            return (true, SanitizeMarkdown(stdout), null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, $"Haiku timed out after {HaikuTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string SanitizeMarkdown(string raw)
    {
        var trimmed = raw.Trim();
        // Strip a wrapping ```markdown ... ``` fence if Haiku adds one despite instructions.
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    private static void WriteAllTextWithRetry(string filePath, string content)
    {
        const int maxAttempts = 8;
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        IOException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts - 1)
            {
                last = ex;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (last != null) throw last;
    }
}
