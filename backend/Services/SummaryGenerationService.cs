using System.Collections.Concurrent;
using System.Diagnostics;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services;

/// <summary>
/// Builds the post-run <c>status.md</c> protocol by handing the tail of the CLI
/// output log to a one-shot Claude Haiku subprocess. Runs fire-and-forget after
/// each successful CLI completion. State is in-memory only — after a backend
/// restart, jobs whose summary was mid-flight fall back to <c>None|Ready</c>
/// based on whether <c>status.md</c> exists on disk.
/// </summary>
public sealed class SummaryGenerationService
{
    private const int MaxLogChars = 60_000;
    private const int HaikuTimeoutSeconds = 90;

    private readonly ILogger<SummaryGenerationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, JobSummaryState> _states = new();

    public SummaryGenerationService(ILogger<SummaryGenerationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public JobSummaryState? GetState(string jobKey)
        => _states.TryGetValue(jobKey, out var s) ? s : null;

    public async Task GenerateAsync(JobInfo info, CancellationToken ct = default)
    {
        var key = info.JobKey;
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
                Fail(key, "cli-output.log not found");
                return;
            }

            var rawLog = await File.ReadAllTextAsync(logPath, ct);
            var truncated = TruncateTail(rawLog, MaxLogChars);
            var prompt = BuildPrompt(truncated);

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
        return "[…earlier output truncated]\n" + tail;
    }

    private static string BuildPrompt(string log) =>
        """
        Du bist ein technischer Protokollant. Aus dem nachfolgenden Agenten-Log
        erzeuge eine knappe deutschsprachige Zusammenfassung als Markdown.

        Gliederung exakt wie folgt (keine zusätzlichen H1):

        # Status

        - Ergebnis: <Erfolg|Teilweise|Fehlgeschlagen>
        - Dauer: <z. B. 4 min>

        ## Was wurde gemacht
        - 3–7 Bullet-Punkte mit konkreten Aktionen (Dateien, Befehle, Ergebnisse).

        ## Offene Punkte
        - 0–5 Bullet-Punkte oder „Keine".

        ## Auffälligkeiten
        - 0–3 Bullet-Punkte mit Warnungen, Fehlern, Workarounds; sonst weglassen.

        ## Bilder
        - Wenn im Log Pfade auf `attachments/*.png|jpg|webp` o. Ä. vorkommen,
          liste sie als `![](attachments/<name>)`. Sonst Sektion weglassen.

        Regeln:
        - Keine Floskeln, kein Marketing-Ton.
        - Pfade und Befehle in `Backticks`.
        - Maximal 250 Wörter Text (Bilder zählen nicht).
        - Antworte ausschließlich mit dem Markdown — keine Vorrede, keine Code-Fences drumherum.

        LOG:
        """ + "\n" + log;

    private async Task<(bool Ok, string? Summary, string? Error)> RunHaikuAsync(
        string prompt, string workingDirectory, CancellationToken ct)
    {
        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";
        var model = _configuration["ClaudeCli:SummaryModel"] ?? "claude-haiku-4-5";

        var psi = new ProcessStartInfo
        {
            FileName = CliExecutionServiceBase.ResolveExecutable(claudePath),
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, null, "Process.Start returned null");

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
