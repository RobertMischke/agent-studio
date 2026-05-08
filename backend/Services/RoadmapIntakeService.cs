using System.Diagnostics;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.AdHoc;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services;

/// <summary>
/// Splits a free-text chat dump into reviewable roadmap candidates by
/// handing the text to a fast Haiku-class subprocess, then materialises
/// the user-confirmed subset as job folders. The split itself is
/// side-effect free; only <see cref="ConfirmAsync"/> writes to disk, and
/// it always lands jobs in <c>1-preparation</c> so the user reviews on
/// the board before queueing - intake never auto-queues to
/// <c>2-ready</c>.
///
/// <para>The Haiku invocation reuses the stdin pattern from
/// <see cref="SummaryGenerationService"/> (the rendered prompt embeds the
/// user dump and would overrun Windows' CreateProcess command-line cap if
/// passed positionally).</para>
///
/// <para>The splitter call is exposed as a virtual hook
/// (<see cref="InvokeSplitterAsync"/>) so tests can stub the model out
/// and exercise the parsing / endpoint contract without billing tokens.</para>
/// </summary>
public class RoadmapIntakeService
{
    private const int MaxInputChars = 40_000;
    private const int HaikuTimeoutSeconds = 60;

    private readonly ILogger<RoadmapIntakeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly JobMutationService _mutations;
    private readonly AdHocUsageRecorder? _usage;

    public RoadmapIntakeService(
        ILogger<RoadmapIntakeService> logger,
        IConfiguration configuration,
        RuntimePromptService prompts,
        JobMutationService mutations,
        AdHocUsageRecorder? usage = null)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
        _mutations = mutations;
        _usage = usage;
    }

    public const string TemplateName = "roadmap-intake.md";

    /// <summary>
    /// Run the splitter against <paramref name="text"/>. Returns an empty
    /// candidate list when the input is empty / whitespace - callers do
    /// not need to special-case that.
    /// </summary>
    public virtual async Task<RoadmapIntakeResponse> SplitAsync(string text, CancellationToken ct = default)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new RoadmapIntakeResponse();

        var bounded = trimmed.Length > MaxInputChars
            ? trimmed[..MaxInputChars] + "\n\n[input truncated - was longer than the splitter accepts]"
            : trimmed;

        var prompt = _prompts.Render(TemplateName,
            new Dictionary<string, string?> { ["input"] = bounded });

        var sw = AdHocClaudeInvoker.StartTiming();
        var (ok, raw, error) = await InvokeSplitterAsync(prompt, ct);
        sw.Stop();

        var fallbackModel = _configuration["RoadmapIntake:Model"]
                            ?? _configuration["ClaudeCli:SummaryModel"]
                            ?? "claude-haiku-4-5";
        var (resultText, usage) = AdHocClaudeInvoker.ParseOrFallback(raw, fallbackModel);
        AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.RoadmapIntake, fallbackModel, usage, sw.ElapsedMilliseconds, ok);

        if (!ok || string.IsNullOrWhiteSpace(resultText))
            throw new InvalidOperationException(error ?? "Splitter returned empty response");

        return ParseSplitterJson(resultText);
    }

    /// <summary>
    /// Materialise <paramref name="request"/>'s candidates as job folders
    /// in <c>1-preparation</c>. Skips empty / invalid candidates and
    /// reports them in the response so the UI can surface a partial
    /// success without hiding the failures.
    /// </summary>
    public RoadmapIntakeConfirmResponse Confirm(RoadmapIntakeConfirmRequest request)
    {
        var created = new List<RoadmapIntakeCreatedJob>();
        var skipped = new List<string>();
        var orderBase = 0;

        foreach (var candidate in request.Candidates ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate.Title))
            {
                skipped.Add("(empty title)");
                continue;
            }

            var cli = CliTypes.IsValid(candidate.SuggestedCliType)
                ? CliTypes.Normalize(candidate.SuggestedCliType)
                : CliTypes.Claude;

            // Preserve the user's reviewed sequence by re-stamping orders
            // sequentially; the splitter's hint is advisory and the user
            // can re-order on the board afterwards.
            orderBase += 10;
            var order = candidate.SuggestedOrder > 0 ? candidate.SuggestedOrder : orderBase;

            var createReq = new CreateJobRequest
            {
                Title = candidate.Title.Trim(),
                Order = order,
                Agent = "claude",
                WatchPath = request.WatchPath,
                PromptMarkdown = BuildPromptBody(candidate),
                CliType = cli,
                TargetState = JobStates.Preparation
            };

            var jobId = _mutations.CreateJob(createReq);
            if (string.IsNullOrEmpty(jobId))
            {
                skipped.Add(candidate.Title);
                continue;
            }

            created.Add(new RoadmapIntakeCreatedJob
            {
                JobId = jobId,
                Title = candidate.Title,
                State = JobStates.Preparation
            });
        }

        return new RoadmapIntakeConfirmResponse
        {
            Created = created,
            Skipped = skipped
        };
    }

    private static string BuildPromptBody(RoadmapIntakeCandidate candidate)
    {
        var body = candidate.PromptBody?.TrimEnd() ?? "";
        if (body.Length == 0) body = candidate.Title;

        var rationale = string.IsNullOrWhiteSpace(candidate.Rationale)
            ? ""
            : $"\n\n---\n\n_Splitter note ({candidate.Kind}): {candidate.Rationale.Trim()}_\n";

        return body + rationale;
    }

    /// <summary>
    /// Parse the splitter's raw text into the structured response. Tolerant
    /// of (but does not require) wrapping ```json fences - the prompt
    /// forbids them but Haiku occasionally adds them anyway.
    /// </summary>
    public static RoadmapIntakeResponse ParseSplitterJson(string raw)
    {
        var json = StripJsonFence(raw.Trim());
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var candidates = new List<RoadmapIntakeCandidate>();
            if (root.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    candidates.Add(new RoadmapIntakeCandidate
                    {
                        Title = ReadString(el, "title"),
                        PromptBody = ReadString(el, "promptBody"),
                        Kind = ReadString(el, "kind", "feature"),
                        SuggestedOrder = ReadInt(el, "suggestedOrder", 10),
                        SuggestedCliType = ReadString(el, "suggestedCliType", CliTypes.Claude),
                        Rationale = ReadString(el, "rationale")
                    });
                }
            }
            var notes = ReadString(root, "notes");
            return new RoadmapIntakeResponse { Candidates = candidates, Notes = notes };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Splitter returned invalid JSON: {ex.Message}", ex);
        }
    }

    private static string StripJsonFence(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        var inner = s[(firstNewline + 1)..];
        if (inner.EndsWith("```")) inner = inner[..^3];
        return inner.Trim();
    }

    private static string ReadString(JsonElement el, string name, string fallback = "")
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(name, out var prop)) return fallback;
        return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? fallback) : fallback;
    }

    private static int ReadInt(JsonElement el, string name, int fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(name, out var prop)) return fallback;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var s) => s,
            _ => fallback
        };
    }

    /// <summary>
    /// Spawn the Haiku subprocess and return its stdout. Override in tests
    /// to substitute a deterministic response without billing tokens.
    /// </summary>
    protected virtual async Task<(bool Ok, string? Raw, string? Error)> InvokeSplitterAsync(
        string prompt, CancellationToken ct)
    {
        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";
        var model = _configuration["RoadmapIntake:Model"]
                    ?? _configuration["ClaudeCli:SummaryModel"]
                    ?? "claude-haiku-4-5";

        var psi = new ProcessStartInfo
        {
            FileName = CliExecutionServiceBase.ResolveExecutable(claudePath),
            WorkingDirectory = Directory.GetCurrentDirectory(),
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

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, null, "Process.Start returned null");

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

            return (true, stdout, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, $"Splitter timed out after {HaikuTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Roadmap intake splitter invocation failed");
            return (false, null, ex.Message);
        }
    }
}
