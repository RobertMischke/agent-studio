using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.AdHoc;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Cli.OneShot;
using OrchestratorApi.Services.Runner;

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
    private readonly AdHocUsageRecorder? _usage;
    private readonly ConcurrentDictionary<string, TaskSummaryState> _states = new();

    public SummaryGenerationService(ILogger<SummaryGenerationService> logger, IConfiguration configuration)
        : this(logger, configuration, new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance), null)
    {
    }

    public SummaryGenerationService(
        ILogger<SummaryGenerationService> logger,
        IConfiguration configuration,
        RuntimePromptService prompts,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
    }

    private readonly CliOneShotRegistry? _oneShotRegistry;

    public TaskSummaryState? GetState(string jobKey)
        => _states.TryGetValue(jobKey, out var s) ? s : null;

    /// <summary>
    /// Pure inflight check used by <see cref="GenerateAsync"/> and exposed
    /// for tests. A job is considered "still generating" when its previous
    /// state is <see cref="TaskSummaryStatus.Generating"/> AND the
    /// <see cref="TaskSummaryState.StartedAt"/> is younger than the Haiku
    /// timeout. Older Generating entries are treated as stuck and
    /// overwritten so the user can recover via the regenerate button.
    /// </summary>
    public static bool IsInflight(TaskSummaryState? prev, DateTime nowUtc, int timeoutSeconds)
    {
        if (prev is null) return false;
        if (prev.Status != TaskSummaryStatus.Generating) return false;
        if (prev.StartedAt is null) return false;
        return (nowUtc - prev.StartedAt.Value).TotalSeconds < timeoutSeconds;
    }

    public Task GenerateAsync(TaskInfo info, CancellationToken ct = default)
        => GenerateAsync(info, runOutcome: null, ct);

    public async Task GenerateAsync(TaskInfo info, TerminalRunOutcome? runOutcome, CancellationToken ct = default)
    {
        var key = info.TaskKey;

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

        _states[key] = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Generating,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath))
            {
                Fail(key, "No CLI output to summarise yet. The task has not been run (logs/cli-output.log is missing). Start it once, then try again.");
                return;
            }

            var rawLog = await File.ReadAllTextAsync(logPath, ct);
            var truncated = TruncateTail(rawLog, MaxLogChars);
            runOutcome ??= TerminalRunOutcomeClassifier.TryClassifyRenderedLog(rawLog)?.Outcome;
            var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol,
                new Dictionary<string, string?> { ["log"] = truncated });

            var (ok, summary, error) = await RunHaikuAsync(prompt, info.FolderPath, ct);
            if (!ok || string.IsNullOrWhiteSpace(summary))
            {
                Fail(key, error ?? "Empty Haiku response");
                return;
            }

            if (runOutcome != null)
            {
                summary = ApplyOutcomeResultLine(summary, runOutcome.ProtocolResult);
            }

            var target = Path.Combine(info.FolderPath, "status.md");
            WriteAllTextWithRetry(target, summary);

            _states[key] = new TaskSummaryState
            {
                Status = TaskSummaryStatus.Ready,
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

    /// <summary>
    /// One-shot interim summary against the current cli-output.log. Unlike
    /// <see cref="GenerateAsync"/>, this method:
    ///   - returns the Haiku markdown to the caller instead of writing it to
    ///     <c>status.md</c> (the post-run summary still owns that file),
    ///   - does not update <see cref="_states"/>, so the protocol-pane's
    ///     "Ready / Generating / Failed" state stays anchored to the real run
    ///     summary,
    ///   - does not apply the deterministic <c>Result:</c> rewrite, because
    ///     the run is still in flight and there is no terminal outcome yet.
    /// Used by the "Interim status" button surfaced in the protocol pane
    /// while a run is alive so the user can peek at progress without
    /// stopping the agent.
    /// </summary>
    public async Task<InterimSummaryResult> GenerateInterimAsync(TaskInfo info, CancellationToken ct = default)
    {
        var logPath = TaskPaths.CliOutputLog(info.FolderPath);
        if (!File.Exists(logPath))
        {
            return InterimSummaryResult.Failure("No CLI output to summarise yet. Start the task once, then try again.");
        }

        string rawLog;
        try
        {
            rawLog = await File.ReadAllTextAsync(logPath, ct);
        }
        catch (Exception ex)
        {
            return InterimSummaryResult.Failure($"Could not read cli-output.log: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(rawLog))
        {
            return InterimSummaryResult.Failure("cli-output.log is empty - the agent hasn't streamed any output yet.");
        }

        var truncated = TruncateTail(rawLog, MaxLogChars);
        var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol,
            new Dictionary<string, string?> { ["log"] = truncated });

        var sw = Stopwatch.StartNew();
        var (ok, summary, error) = await RunHaikuAsync(prompt, info.FolderPath, ct);
        sw.Stop();

        if (!ok || string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogInformation("Interim summary failed for {JobId} after {ElapsedMs}ms: {Error}",
                info.Id, sw.ElapsedMilliseconds, error);
            return InterimSummaryResult.Failure(error ?? "Empty Haiku response");
        }

        _logger.LogInformation("Interim summary produced for {JobId} ({Bytes} bytes, {ElapsedMs}ms)",
            info.Id, summary.Length, sw.ElapsedMilliseconds);
        return InterimSummaryResult.Success(summary, sw.ElapsedMilliseconds);
    }

    private void Fail(string key, string error)
    {
        var prev = _states.TryGetValue(key, out var s) ? s : new TaskSummaryState();
        _states[key] = prev with
        {
            Status = TaskSummaryStatus.Failed,
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
        var model = _configuration["ClaudeCli:SummaryModel"] ?? "claude-haiku-4-5";

        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                Timeout = TimeSpan.FromSeconds(HaikuTimeoutSeconds),
                Source = AdHocUsageSources.SummaryGeneration,
                RecordUsage = false, // We record below with parsed text + usage
            }, ct).ConfigureAwait(false);

            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, r.Usage,
                (long)r.Duration.TotalMilliseconds, ok: r.Ok);
            if (!r.Ok) return (false, null, r.Error);
            return (true, SanitizeMarkdown(r.ParsedText), null);
        }

        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";

        // Feed the prompt via stdin instead of a positional `-p <prompt>`
        // argument. See OneShot service for the production path; this
        // fallback is for tests that build the service without DI.
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
        foreach (var arg in AdHocClaudeInvoker.BuildArgs(model)) psi.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();
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
            sw.Stop();
            if (p.ExitCode != 0)
            {
                AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, null, sw.ElapsedMilliseconds, ok: false);
                return (false, null, $"claude exited {p.ExitCode}: {stderr.Trim()}");
            }

            var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(stdout, model);
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, usage, sw.ElapsedMilliseconds, ok: true);
            return (true, SanitizeMarkdown(text), null);
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

    public static string ApplyOutcomeResultLine(string markdown, string protocolResult)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(protocolResult)) return markdown;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("- Result:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"- Result: {protocolResult}";
                return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            }
        }

        var statusIndex = lines.FindIndex(l => string.Equals(l.Trim(), "# Status", StringComparison.OrdinalIgnoreCase));
        if (statusIndex >= 0)
        {
            lines.Insert(statusIndex + 1, "");
            lines.Insert(statusIndex + 2, $"- Result: {protocolResult}");
            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        lines.Insert(0, $"- Result: {protocolResult}");
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
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

/// <summary>
/// Result of <see cref="SummaryGenerationService.GenerateInterimAsync"/>.
/// On success carries the Haiku markdown and the call duration so the UI
/// can show how long the peek took; on failure carries a user-facing error
/// string that the frontend renders in the interim-summary banner.
/// </summary>
public sealed record InterimSummaryResult(bool Ok, string? Markdown, string? Error, long DurationMs)
{
    public static InterimSummaryResult Success(string markdown, long durationMs)
        => new(true, markdown, null, durationMs);

    public static InterimSummaryResult Failure(string error)
        => new(false, null, error, 0);
}
