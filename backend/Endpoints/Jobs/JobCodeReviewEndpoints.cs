using Microsoft.Extensions.Configuration;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Review;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// User-triggered code-review step. Posts a single review pass against
/// the job's most recent commit using the chosen model (or the
/// configured default), writes <c>code-review-{utc-ts}.md</c> into the
/// job folder, and merges a <c>code-review:&lt;verdict&gt;</c> tag onto
/// the job.
///
/// Sibling to the auto-review pipeline that runs in 4-auto-review:
/// auto-review is policy-driven and aggregates four narrow aspects;
/// this endpoint is the user's escape hatch to run one focused review
/// with a model they pick. Lane state is never touched - verdicts
/// surface as tags only so the user keeps the final say.
/// </summary>
public static class JobCodeReviewEndpoints
{
    /// <summary>Configuration key for the default LLM model.</summary>
    public const string DefaultModelConfigKey = "CodeReviewStep:DefaultModel";

    /// <summary>Configuration key for the default CLI type.</summary>
    public const string DefaultCliConfigKey = "CodeReviewStep:DefaultCli";

    /// <summary>Configuration key for the per-run wall-clock cap.</summary>
    public const string TimeoutSecondsConfigKey = "CodeReviewStep:PerRunTimeoutSeconds";

    /// <summary>Hard fallback when neither config nor request specifies a CLI.</summary>
    public const string DefaultCliFallback = "claude";

    /// <summary>Hard fallback when neither config nor request specifies a model.</summary>
    public const string DefaultModelFallback = "claude-opus-4-7";

    /// <summary>Default per-run wall-clock cap when configuration omits it.</summary>
    public const int DefaultTimeoutSecondsFallback = 600;

    public static void MapJobCodeReviewEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/jobs/{jobId}/code-review
        // Body: { "watchPath"?: string, "model"?: string, "cliType"?: string, "commit"?: string }
        // Synchronous: returns the report once the review finishes.
        group.MapPost("/{jobId}/code-review",
            async (string jobId,
                   string? watchPath,
                   CodeReviewStepEndpointRequest? body,
                   JobScannerService scanner,
                   GitService git,
                   CodeReviewStepService service,
                   IConfiguration configuration,
                   CancellationToken ct) =>
        {
            var resolvedWatchPath = body?.WatchPath ?? watchPath;
            var info = scanner.FindJob(jobId, resolvedWatchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            var detail = scanner.GetJobDetail(jobId, resolvedWatchPath);
            var taskBody = detail?.PromptMarkdown ?? string.Empty;

            var commit = !string.IsNullOrWhiteSpace(body?.Commit)
                ? body!.Commit!
                : git.GetHeadSha(jobId, resolvedWatchPath) ?? string.Empty;

            string diff;
            if (string.IsNullOrWhiteSpace(commit))
            {
                diff = "(no commit resolved; reviewing working-tree diff)\n\n" +
                       git.GetDiff(jobId, resolvedWatchPath, path: null);
            }
            else
            {
                diff = git.GetCommitDiff(jobId, resolvedWatchPath, commit, path: null);
            }

            var cli = !string.IsNullOrWhiteSpace(body?.CliType)
                ? body!.CliType!
                : configuration[DefaultCliConfigKey] ?? DefaultCliFallback;
            var model = !string.IsNullOrWhiteSpace(body?.Model)
                ? body!.Model!
                : configuration[DefaultModelConfigKey] ?? DefaultModelFallback;
            var timeoutSeconds = configuration.GetValue<int?>(TimeoutSecondsConfigKey)
                ?? DefaultTimeoutSecondsFallback;

            var request = new CodeReviewStepRequest(
                Project: info.ProjectName ?? string.Empty,
                JobId: info.Id,
                JobTitle: info.Title ?? string.Empty,
                JobFolderPath: info.FolderPath,
                TaskBody: taskBody,
                Diff: diff,
                CliType: cli,
                Model: model)
            {
                Commit = string.IsNullOrWhiteSpace(commit) ? null : commit,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            };

            var report = await service.RunAsync(request, ct);

            return Results.Ok(new CodeReviewStepEndpointResponse
            {
                FileName = report.FileName,
                Verdict = AspectVerdictParsing.StatusToken(report.Status),
                Summary = report.Summary,
                Model = report.Model,
                CliType = report.CliType,
                Commit = report.Commit,
                ConcernTagId = report.ConcernTagId,
                DurationMs = report.DurationMs,
                StartedAt = report.StartedAt,
            });
        });
    }
}

/// <summary>Body for <c>POST /api/jobs/{jobId}/code-review</c>. All fields optional.</summary>
public sealed record CodeReviewStepEndpointRequest
{
    public string? WatchPath { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? Commit { get; init; }
}

/// <summary>Response shape for the code-review endpoint.</summary>
public sealed record CodeReviewStepEndpointResponse
{
    public required string FileName { get; init; }
    public required string Verdict { get; init; }
    public required string Summary { get; init; }
    public required string Model { get; init; }
    public required string CliType { get; init; }
    public string? Commit { get; init; }
    public string? ConcernTagId { get; init; }
    public required long DurationMs { get; init; }
    public required DateTime StartedAt { get; init; }
}
