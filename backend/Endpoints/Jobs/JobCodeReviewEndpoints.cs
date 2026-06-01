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
public static class TaskCodeReviewEndpoints
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

    /// <summary>
    /// Resolve the effective code-review default CLI + model from
    /// configuration, falling back to the hard-coded defaults. Pure so the
    /// resolution can be unit-tested without spinning an HTTP host.
    /// </summary>
    public static (string CliType, string Model) ResolveDefaults(IConfiguration configuration)
    {
        var cli = configuration[DefaultCliConfigKey];
        var model = configuration[DefaultModelConfigKey];
        return (
            string.IsNullOrWhiteSpace(cli) ? DefaultCliFallback : cli!,
            string.IsNullOrWhiteSpace(model) ? DefaultModelFallback : model!);
    }

    public static void MapJobCodeReviewEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/tasks/code-review/defaults
        // The configured default CLI + model for the user-triggered review
        // step. The panel seeds its picker from this when the operator has
        // no remembered last-used pair, so a deployment-level
        // CodeReviewStep:DefaultModel actually shows up in the UI instead of
        // a hard-coded guess. Both literal segments, so it never collides
        // with the parameterised /{jobId}/code-review/... routes.
        group.MapGet("/code-review/defaults", (IConfiguration configuration) =>
        {
            var (cli, model) = ResolveDefaults(configuration);
            return Results.Ok(new CodeReviewDefaultsResponse { CliType = cli, Model = model });
        });

        // GET /api/tasks/{jobId}/code-review/list
        // Returns the list of code-review-*.md artifacts in the job folder,
        // newest-first. Each entry carries the parsed frontmatter so the
        // frontend can render verdict + summary without fetching each file.
        group.MapGet("/{jobId}/code-review/list",
            (string jobId, string? watchPath, TaskScannerService scanner) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            var entries = new List<CodeReviewListEntry>();
            try
            {
                if (Directory.Exists(info.FolderPath))
                {
                    foreach (var path in Directory.EnumerateFiles(info.FolderPath, "code-review-*.md"))
                    {
                        var fileName = Path.GetFileName(path);
                        try
                        {
                            var content = File.ReadAllText(path);
                            var fm = OrchestratorApi.Services.Markdown.FrontmatterParser.Parse(content);
                            var fields = fm.Ok ? fm.Fields : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            entries.Add(new CodeReviewListEntry
                            {
                                FileName = fileName,
                                Verdict = fields.GetValueOrDefault("verdict") ?? "unknown",
                                Summary = fields.GetValueOrDefault("summary") ?? string.Empty,
                                Model = fields.GetValueOrDefault("model") ?? string.Empty,
                                CliType = fields.GetValueOrDefault("cliType") ?? string.Empty,
                                Commit = fields.GetValueOrDefault("commit"),
                                RunAt = fields.GetValueOrDefault("runAt") ?? string.Empty,
                            });
                        }
                        catch
                        {
                            // Skip unreadable files; they should not break the list.
                        }
                    }
                }
            }
            catch
            {
                // Folder enumeration failure: return empty list rather than 500.
            }

            entries.Sort((a, b) => string.CompareOrdinal(b.RunAt, a.RunAt));
            return Results.Ok(new CodeReviewListResponse { Entries = entries });
        });

        // GET /api/tasks/{jobId}/code-review/{fileName}
        // Returns the raw MD body for one review. The caller passes a file
        // name that came from the list endpoint; we never accept arbitrary
        // paths.
        group.MapGet("/{jobId}/code-review/{fileName}",
            (string jobId, string fileName, string? watchPath, TaskScannerService scanner) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = $"No job '{jobId}'" });

            // Path-traversal guard: only accept files whose name matches the
            // expected pattern and that resolve inside the job folder.
            if (string.IsNullOrWhiteSpace(fileName)
                || !fileName.StartsWith("code-review-", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            {
                return Results.BadRequest(new { error = "Invalid review file name." });
            }

            var path = Path.Combine(info.FolderPath, fileName);
            if (!File.Exists(path)) return Results.NotFound(new { error = "Review not found." });

            try
            {
                return Results.Ok(new { fileName, content = File.ReadAllText(path) });
            }
            catch
            {
                return Results.NotFound(new { error = "Review unreadable." });
            }
        });

        // POST /api/tasks/{jobId}/code-review
        // Body: { "watchPath"?: string, "model"?: string, "cliType"?: string, "commit"?: string }
        // Synchronous: returns the report once the review finishes.
        group.MapPost("/{jobId}/code-review",
            async (string jobId,
                   string? watchPath,
                   CodeReviewStepEndpointRequest? body,
                   TaskScannerService scanner,
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

/// <summary>Response for <c>GET /api/tasks/code-review/defaults</c>.</summary>
public sealed record CodeReviewDefaultsResponse
{
    public required string CliType { get; init; }
    public required string Model { get; init; }
}

/// <summary>One row in the per-job code-review listing.</summary>
public sealed record CodeReviewListEntry
{
    public required string FileName { get; init; }
    public required string Verdict { get; init; }
    public required string Summary { get; init; }
    public required string Model { get; init; }
    public required string CliType { get; init; }
    public string? Commit { get; init; }
    public required string RunAt { get; init; }
}

/// <summary>Response for <c>GET /api/tasks/{jobId}/code-review/list</c>.</summary>
public sealed record CodeReviewListResponse
{
    public required IReadOnlyList<CodeReviewListEntry> Entries { get; init; }
}

/// <summary>Body for <c>POST /api/tasks/{jobId}/code-review</c>. All fields optional.</summary>
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
