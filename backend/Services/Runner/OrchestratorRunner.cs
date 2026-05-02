using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Result of an orchestrator decision call: the orchestrator's reply text
/// (the follow-up the user would have typed if they were here), token
/// usage for the call, the captured Claude session id (so the runner can
/// resume the same session on the next call), and a flag for whether the
/// underlying CLI errored.
/// </summary>
public sealed record OrchestratorDecisionResult(
    bool Success,
    string ReplyText,
    string Model,
    OrchestratorTokenUsage? TokenUsage,
    string? CapturedSessionId,
    string? ErrorMessage);

/// <summary>
/// Invokes the Claude CLI in one-shot JSON mode to produce an orchestrator
/// decision for the user when the active agent emits
/// <c>[[TASK_NEEDS_INPUT:...]]</c> in auto mode (Phase E and later). The
/// CLI returns a single JSON document with the result text plus token
/// usage, both of which we capture and surface in the orchestrator log.
///
/// <para>
/// Why a separate class instead of reusing <see cref="ClaudeCliService"/>:
/// the existing CLI service is built for the long-running streaming
/// task-execution path. The orchestrator's decision calls are short,
/// one-shot, and need exact token-usage capture from the JSON envelope.
/// Mixing those concerns into ClaudeCliService would force every existing
/// streaming run through the JSON parser path. Cleaner to keep the
/// orchestrator runtime as its own thin shell.
/// </para>
/// </summary>
public class OrchestratorRunner
{
    public const string DefaultModel = "claude-opus-4-7";

    private readonly ClaudeCliService _claude;
    private readonly ILogger<OrchestratorRunner> _logger;

    public OrchestratorRunner(ClaudeCliService claude, ILogger<OrchestratorRunner> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    /// <summary>
    /// Run the orchestrator. <paramref name="prompt"/> is the full prompt
    /// (system framing + situation summary + question). The model defaults
    /// to <see cref="DefaultModel"/> if the caller doesn't override it
    /// from project settings.
    /// </summary>
    public Task<OrchestratorDecisionResult> DecideAsync(
        string prompt,
        string? model,
        string workingDirectory,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: null, ct);

    /// <summary>
    /// Resume an existing orchestrator session via <c>claude -r &lt;sessionId&gt;</c>.
    /// The session keeps the boot-time context (project facts, recent
    /// activity) and accumulates conversation history, so subsequent
    /// decisions cost less on framing.
    /// </summary>
    public Task<OrchestratorDecisionResult> ResumeAsync(
        string sessionId,
        string prompt,
        string? model,
        string workingDirectory,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: sessionId, ct);

    private async Task<OrchestratorDecisionResult> InvokeAsync(
        string prompt,
        string? model,
        string workingDirectory,
        string? resumeSessionId,
        CancellationToken ct)
    {
        var modelId = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();

        var args = new List<string>
        {
            "-p", Quote(prompt),
            "--output-format", "json",
            "--model", Quote(modelId),
            "--dangerously-skip-permissions"
        };
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("-r");
            args.Add(Quote(resumeSessionId!));
        }

        var psi = new ProcessStartInfo
        {
            FileName = CliExecutionServiceBase.ResolveExecutable(_claude.GetCliPath()),
            Arguments = string.Join(' ', args),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            try { process.StandardInput.Close(); } catch { }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"claude CLI exited with code {process.ExitCode}" : stderr.Trim();
                _logger.LogWarning("Orchestrator decision failed: exit={Exit}, stderr={Stderr}", process.ExitCode, msg);
                return new OrchestratorDecisionResult(false, "", modelId, null, null, msg);
            }

            return ParseResult(stdout, modelId);
        }
        catch (OperationCanceledException)
        {
            return new OrchestratorDecisionResult(false, "", modelId, null, null, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator decision call failed to spawn or read");
            return new OrchestratorDecisionResult(false, "", modelId, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Parse the single JSON document <c>claude -p ... --output-format json</c>
    /// emits at completion. Shape (validated against live CLI):
    /// <c>{ type: "result", subtype: "success", is_error, result, session_id,
    /// total_cost_usd, usage: { input_tokens, cache_creation_input_tokens,
    /// cache_read_input_tokens, output_tokens }, model? }</c>.
    /// Tolerant: missing fields default to zero / empty.
    /// </summary>
    public static OrchestratorDecisionResult ParseResult(string stdout, string modelId)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return new OrchestratorDecisionResult(false, "", modelId, null, null, "empty stdout");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var resultText = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            string? declaredModel = root.TryGetProperty("model", out var md) ? md.GetString() : null;
            string? sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;

            OrchestratorTokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new OrchestratorTokenUsage
                {
                    Model = declaredModel ?? modelId,
                    InputTokens          = TryGetInt(u, "input_tokens"),
                    OutputTokens         = TryGetInt(u, "output_tokens"),
                    CacheReadTokens      = TryGetInt(u, "cache_read_input_tokens"),
                    CacheCreationTokens  = TryGetInt(u, "cache_creation_input_tokens")
                };
            }

            return new OrchestratorDecisionResult(
                Success: !isError && !string.IsNullOrWhiteSpace(resultText),
                ReplyText: resultText.Trim(),
                Model: declaredModel ?? modelId,
                TokenUsage: usage,
                CapturedSessionId: string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                ErrorMessage: isError ? "is_error=true in CLI output" : null);
        }
        catch (Exception ex)
        {
            return new OrchestratorDecisionResult(false, "", modelId, null, null, $"parse failed: {ex.Message}");
        }
    }

    private static int TryGetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return 0;
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
