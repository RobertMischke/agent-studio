using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

/// <summary>
/// One raw step-prompt record. Captures the FINAL prompt text a one-shot
/// step-call (aspect, code-review-grade, orchestrator-decision, drift,
/// ...) handed to the CLI, plus the minimal provenance needed to attribute
/// it: the pipeline step id, the runtime template it was rendered from, the
/// model, the CLI, the usage source, and the dispatch timestamp.
///
/// <para>
/// This is the "Rohdaten" side of the
/// "raw data complete, derivation as read-model" principle: the prompt is
/// written once, raw, at the central dispatch point. Main-run prompts and
/// follow-ups are deliberately NOT recorded here - they already live in the
/// task's <c>prompt.md</c> / chat, so logging them again would be double
/// bookkeeping.
/// </para>
/// </summary>
public sealed record StepPromptEntry
{
    /// <summary>UTC dispatch time (when the prompt was sent to the CLI).</summary>
    public DateTime At { get; init; }

    /// <summary>Pipeline step id, e.g. <c>aspect-requirement-fit</c> or
    /// <c>post-code-review-grade</c>. The read-model keys off this so the UI
    /// can show the prompt next to the matching step / timeline entry.</summary>
    public string StepId { get; init; } = string.Empty;

    /// <summary>Runtime prompt template the final text was rendered from,
    /// e.g. <c>review-aspect-requirement-fit.md</c>. Null for steps whose
    /// prompt is built inline.</summary>
    public string? TemplateRef { get; init; }

    /// <summary>Model the prompt was sent to.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>CLI the prompt was sent through (lowercase, e.g. <c>claude</c>).</summary>
    public string Cli { get; init; } = string.Empty;

    /// <summary>Usage-attribution source tag (e.g. <c>review-decision</c>),
    /// when the call site supplied one.</summary>
    public string? Source { get; init; }

    /// <summary>The final, raw prompt text exactly as piped to the CLI.</summary>
    public string Prompt { get; init; } = string.Empty;
}

/// <summary>
/// Per-job append-only writer + reader for <c>.metadata/prompts.jsonl</c>:
/// the raw record of every step-call prompt dispatched for a task. Writing
/// goes through the shared <see cref="IJsonlAppender"/> so concurrent aspect
/// fan-out cannot interleave bytes; reading parses the file back into a
/// read-model the pipeline / timeline UI consumes via
/// <c>GET /api/tasks/{id}/step-prompts</c>.
///
/// <para>
/// The file lives next to <c>pipeline-execution.json</c> and
/// <c>.metadata/files.json</c> in the job folder - the same sidecar pattern
/// the runtime already uses for step provenance. Persistence is
/// observability: a failed append is logged and swallowed, never propagated
/// into the dispatch path.
/// </para>
/// </summary>
public sealed class StepPromptLog
{
    public const string RelativePath = ".metadata/prompts.jsonl";

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IJsonlAppender _appender;
    private readonly ILogger<StepPromptLog> _logger;

    public StepPromptLog(IJsonlAppender appender, ILogger<StepPromptLog> logger)
    {
        _appender = appender;
        _logger = logger;
    }

    /// <summary>
    /// Append one raw step-prompt record to the job's
    /// <c>.metadata/prompts.jsonl</c>. Best-effort: an IO failure is logged
    /// and swallowed so prompt logging can never break the CLI call it is
    /// observing. No-op when the job folder or step id is missing.
    /// </summary>
    public async Task AppendAsync(string jobFolderPath, StepPromptEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath) || string.IsNullOrWhiteSpace(entry.StepId)) return;
        try
        {
            var path = Path.Combine(jobFolderPath, RelativePath);
            await _appender.AppendAsync(path, entry, options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "step-prompt-log: failed to append prompt for step {StepId} in {JobFolder}",
                entry.StepId, jobFolderPath);
        }
    }

    /// <summary>
    /// Read every recorded step-prompt for the job in chronological (file)
    /// order. Returns an empty list when no prompts were recorded yet; skips
    /// blank or unparseable lines rather than throwing so one bad line never
    /// hides the rest.
    /// </summary>
    public IReadOnlyList<StepPromptEntry> ReadForJob(string jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return [];
        var path = Path.Combine(jobFolderPath, RelativePath);
        if (!File.Exists(path)) return [];

        var entries = new List<StepPromptEntry>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<StepPromptEntry>(line, ReadOpts);
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.StepId)) entries.Add(entry);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "step-prompt-log: skipping unparseable line in {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "step-prompt-log: failed to read {Path}", path);
        }

        return entries;
    }
}
