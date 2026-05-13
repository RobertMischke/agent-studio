using System.Diagnostics;
using OrchestratorApi.Models;
using OrchestratorApi.Services.AdHoc;
using OrchestratorApi.Services.Cli.OneShot;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Review;

/// <summary>
/// User-triggered code review step. Sits next to <see cref="AspectRunnerService"/>
/// but is a separate, single-shot review that the user explicitly plans
/// against a job: pick a model (default from configuration), run one
/// review pass against the job's most recent commit, write a fresh
/// <c>code-review-{utc-ts}.md</c> into the job folder, and merge a
/// <c>code-review:&lt;verdict&gt;</c> tag onto the job so the card shows
/// the outcome. The aspect-runner pipeline that runs in 4-auto-review is
/// deliberately not reused here so the two surfaces stay independent:
/// auto-review is policy-driven and aggregates four narrow aspects;
/// the code-review step is a user-invoked single review against a
/// chosen model.
///
/// <para>
/// Lane state is never touched. Verdicts surface as tags only, so the
/// user keeps the final say on what happens next (matches AGENTS.md
/// "auto-review never moves directly to 6-completed"). Re-running
/// produces a new MD with a fresh timestamp; nothing overwrites.
/// </para>
/// </summary>
public sealed class CodeReviewStepService
{
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<CodeReviewStepService> _logger;
    private readonly AdHocUsageRecorder? _usage;
    private readonly CliOneShotRegistry? _oneShotRegistry;

    /// <summary>
    /// CLI invocation seam. Production wires it through <see cref="ICliOneShot"/>
    /// when a registry is present. Tests substitute a deterministic stub
    /// keyed on the model so they can verify the per-model dispatch
    /// without spinning a subprocess.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;

    public CodeReviewStepService(
        RuntimePromptService prompts,
        ILogger<CodeReviewStepService> logger,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null)
    {
        _prompts = prompts;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;

        if (_oneShotRegistry != null)
        {
            CliRunner = (cli, model, prompt, timeout, ct) =>
                RunViaOneShotAsync(cli, model, prompt, timeout, ct);
        }
    }

    /// <summary>Public for testability: runtime prompt template name.</summary>
    public const string PromptTemplate = "code-review-step.md";

    /// <summary>Public for testability: ad-hoc usage source tag.</summary>
    public const string UsageSource = "code-review-step";

    /// <summary>
    /// Run one code-review pass and persist its evidence. The method is
    /// fail-loud-but-don't-block, mirroring <see cref="AspectRunnerService"/>:
    /// CLI failures and unparseable replies both produce a deterministic
    /// Concerns verdict so the user always sees a result on the card.
    /// </summary>
    public async Task<CodeReviewStepReport> RunAsync(CodeReviewStepRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.JobFolderPath))
            throw new ArgumentException("JobFolderPath is required", nameof(request));

        var startedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "code-review-step: starting project={Project} job={JobId} model={Model} commit={Commit}",
            request.Project, request.JobId, request.Model, request.Commit ?? "(HEAD)");

        var prompt = BuildPrompt(request);

        string rawResponse;
        AspectStatus status;
        string summary;
        OrchestratorTokenUsage? callUsage = null;
        var sw = Stopwatch.StartNew();
        var ok = true;
        try
        {
            rawResponse = await CliRunner(request.CliType, request.Model, prompt, request.Timeout, ct);
            sw.Stop();
            var (parsedText, parsedUsage) = AdHocClaudeInvoker.ParseOrFallback(rawResponse, request.Model);
            rawResponse = parsedText;
            callUsage = parsedUsage;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            ok = false;
            _logger.LogWarning(ex,
                "code-review-step: CLI invocation failed for {Project}/{JobId}; defaulting to concerns",
                request.Project, request.JobId);
            rawResponse = string.Empty;
        }

        AdHocClaudeInvoker.Record(_usage, UsageSource, request.Model, callUsage,
            sw.ElapsedMilliseconds, ok, project: request.Project, jobId: request.JobId);

        var parsed = AspectVerdictParsing.ParseVerdict(rawResponse);
        if (parsed == null)
        {
            // No silent durchwinken: an unparseable reply gets a Concerns
            // verdict so the card always shows an outcome.
            status = AspectStatus.Concerns;
            summary = string.IsNullOrWhiteSpace(rawResponse)
                ? "Code review produced no parseable reply."
                : "Code review produced no parseable verdict sentinel.";
        }
        else
        {
            status = parsed.Value.Status;
            summary = parsed.Value.Summary;
        }

        var verdict = new CodeReviewVerdict(status, summary);
        var fileName = $"code-review-{startedAt:yyyy-MM-ddTHH-mm-ssZ}.md";
        var filePath = Path.Combine(request.JobFolderPath, fileName);

        try
        {
            var report = RenderReport(request, verdict, rawResponse, startedAt);
            await File.WriteAllTextAsync(filePath, report, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-step: failed to write {Path}", filePath);
        }

        var concernTagId = TagFor(status);
        if (concernTagId != null)
        {
            ConcernTagWriter.MergeConcernTags(request.JobFolderPath, new[] { concernTagId }, _logger);
        }

        _logger.LogInformation(
            "code-review-step: finished project={Project} job={JobId} model={Model} verdict={Verdict} durationMs={DurationMs} file={File}",
            request.Project, request.JobId, request.Model, AspectVerdictParsing.StatusToken(status),
            sw.ElapsedMilliseconds, fileName);

        return new CodeReviewStepReport(
            FileName: fileName,
            FilePath: filePath,
            Status: status,
            Summary: summary,
            Model: request.Model,
            CliType: request.CliType,
            Commit: request.Commit,
            ConcernTagId: concernTagId,
            DurationMs: sw.ElapsedMilliseconds,
            StartedAt: startedAt);
    }

    /// <summary>Tag id for the given verdict, or null when no tag should be hung.</summary>
    public static string? TagFor(AspectStatus status) => status switch
    {
        AspectStatus.Pass => "code-review:pass",
        AspectStatus.Concerns => "code-review:concerns",
        AspectStatus.Block => "code-review:block",
        _ => null
    };

    private string BuildPrompt(CodeReviewStepRequest request)
    {
        var values = new Dictionary<string, string?>
        {
            ["project"] = request.Project,
            ["job_id"] = request.JobId,
            ["job_title"] = request.JobTitle,
            ["task_body"] = request.TaskBody,
            ["commit"] = request.Commit ?? "(HEAD)",
            ["diff"] = request.Diff,
            ["model"] = request.Model,
        };
        try
        {
            return _prompts.Render(PromptTemplate, values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-step: prompt template '{Template}' not rendered; using inline fallback",
                PromptTemplate);
            return BuildInlineFallbackPrompt(request);
        }
    }

    private static string BuildInlineFallbackPrompt(CodeReviewStepRequest request)
    {
        return
            $"# Code review step\n\n" +
            $"Review the diff below for **{request.Project}/{request.JobId}** ({request.JobTitle}).\n" +
            $"Commit: `{request.Commit ?? "(HEAD)"}`. Model: `{request.Model}`.\n\n" +
            $"## Task body\n\n```\n{request.TaskBody}\n```\n\n" +
            $"## Diff\n\n```\n{request.Diff}\n```\n\n" +
            "Reply with a short paragraph plus exactly one sentinel on its own line:\n\n" +
            "[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence>]]\n";
    }

    private static string RenderReport(
        CodeReviewStepRequest request,
        CodeReviewVerdict verdict,
        string body,
        DateTime startedAt)
    {
        var statusToken = AspectVerdictParsing.StatusToken(verdict.Status);
        var tag = TagFor(verdict.Status);
        var safeSummary = EscapeYaml(verdict.Summary);
        return
            "---\n" +
            "type: code-review-step\n" +
            $"runAt: {startedAt:O}\n" +
            $"model: {request.Model}\n" +
            $"cliType: {request.CliType}\n" +
            $"commit: {request.Commit ?? "(HEAD)"}\n" +
            $"verdict: {statusToken}\n" +
            $"summary: {safeSummary}\n" +
            (tag is null ? string.Empty : $"tag: {tag}\n") +
            "---\n\n" +
            "# Code Review Step\n\n" +
            $"**Verdict:** {statusToken}\n\n" +
            (string.IsNullOrWhiteSpace(verdict.Summary) ? string.Empty : $"**Summary:** {verdict.Summary}\n\n") +
            $"**Model:** `{request.Model}` (`{request.CliType}`)\n\n" +
            $"**Commit:** `{request.Commit ?? "(HEAD)"}`\n\n" +
            "## Reviewer reply\n\n" +
            (string.IsNullOrWhiteSpace(body)
                ? "_No reply text was returned by the model._\n"
                : "```\n" + body.Trim() + "\n```\n");
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Contains(':') || oneLine.Contains('#') || oneLine.StartsWith('-'))
        {
            return "\"" + oneLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return oneLine;
    }

    private async Task<string> RunViaOneShotAsync(string cliType, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get(cliType);
        if (oneShot == null) return await DefaultRunCliAsync(cliType, model, prompt, timeout, ct);

        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: cliType,
            Model: model,
            Prompt: prompt)
        {
            Timeout = timeout,
            Source = UsageSource,
            RecordUsage = false, // RunAsync caller (this service) records via AdHocClaudeInvoker.Record
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "code-review-step: CLI '{Cli}' returned exit={Exit} duration={DurationMs}ms error={Error}",
                cliType, result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return result.Stdout;
    }

    private static Task<string> DefaultRunCliAsync(
        string cliType, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        // Tests construct the service without an ICliOneShot registry and
        // overwrite CliRunner directly. Production always wires through
        // RunViaOneShotAsync; this fallback exists so the service is
        // constructible in unit tests that only need the parsing path.
        return Task.FromResult(string.Empty);
    }
}

/// <summary>One code-review request: who to review, with which model, against which commit.</summary>
public sealed record CodeReviewStepRequest(
    string Project,
    string JobId,
    string JobTitle,
    string JobFolderPath,
    string TaskBody,
    string Diff,
    string CliType,
    string Model)
{
    /// <summary>Optional commit SHA. When null, the prompt records HEAD.</summary>
    public string? Commit { get; init; }

    /// <summary>Wall-clock cap for the CLI invocation. Defaults to 10 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Per-call report returned by <see cref="CodeReviewStepService.RunAsync"/>.</summary>
public sealed record CodeReviewStepReport(
    string FileName,
    string FilePath,
    AspectStatus Status,
    string Summary,
    string Model,
    string CliType,
    string? Commit,
    string? ConcernTagId,
    long DurationMs,
    DateTime StartedAt);

internal sealed record CodeReviewVerdict(AspectStatus Status, string Summary);
