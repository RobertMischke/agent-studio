using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// CLI execution surface for one job: <c>start</c>, <c>stop</c>,
/// <c>continue</c>, the <c>output</c> buffer the protocol pane polls,
/// the session-event log that drives the "session continued / lost"
/// chip, and the manual <c>summary/regenerate</c> + <c>context-usage/refresh</c>
/// triggers. All routes funnel through <see cref="TaskRunnerService"/>;
/// this file is the HTTP shell.
/// </summary>
public static class JobRunnerEndpoints
{
    public static void MapJobRunnerEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/start", async (string jobId, string? watchPath, StartJobRequest? req, TaskRunnerService runner, JobScannerService scanner, CancellationToken ct) =>
        {
            var job = scanner.FindJob(jobId, watchPath);
            if (job == null)
                return Results.NotFound(new { error = "Job not found" });

            if (job.State is not (JobStates.Ready or JobStates.Progress))
                return Results.BadRequest(new { error = $"Job is in state '{job.State}' - only jobs in 'ready' or 'progress' can be started" });

            try
            {
                var resp = await runner.StartJobAsync(jobId, watchPath, req?.Model, req?.CliType, ct);
                return resp.Status == "queued"
                    ? Results.Accepted(value: resp)
                    : Results.Ok(resp);
            }
            catch (JobOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        group.MapPost("/{jobId}/stop", (string jobId, string? watchPath, TaskRunnerService runner) =>
        {
            var success = runner.StopJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{jobId}/continue", async (string jobId, string? watchPath, ContinueJobRequest req, TaskRunnerService runner, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Prompt))
                return Results.BadRequest(new { error = "Prompt is required" });

            var mode = ContinueModes.Normalize(req.Mode);
            try
            {
                var resp = await runner.ContinueJobAsync(jobId, req.Prompt, watchPath, req.Model, mode, ct);
                return resp.Status == "queued"
                    ? Results.Accepted(value: resp)
                    : Results.Ok(resp);
            }
            catch (JobOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        group.MapGet("/{jobId}/output", (string jobId, string? watchPath, TaskRunnerService runner) =>
        {
            var output = runner.GetJobOutput(jobId, watchPath);
            return Results.Ok(output);
        });

        // Returns the session-event log for a job: one record per start /
        // continue / recovery, with input + captured session ids and a
        // `resumed` boolean. Drives the "session continued / lost" chip and
        // gives the user a paper trail when continuations don't behave as
        // expected. Includes the current sessionChain so the frontend can
        // render a chip without a second round-trip.
        group.MapGet("/{jobId}/session-events", (string jobId, string? watchPath, JobScannerService scanner, JobSessionLog sessions) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            return Results.Ok(new
            {
                events,
                sessionChain = info.SessionChain,
                currentSessionId = info.SessionName
            });
        });

        // The condensed run timeline that drives the protocol-pane redesign.
        // One record per CLI invocation between user inputs, paired with the
        // [taskboard] Started/exited markers in cli-output.log so the frontend
        // can render line-spans for drill-down. See docs/design-principles.md
        // for the contract this surface has to honour: top-level summary +
        // always-available drill-down.
        group.MapGet("/{jobId}/runs", (string jobId, string? watchPath, JobScannerService scanner, JobSessionLog sessions) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            var lines = CliOutputLogParser.ParseFile(JobPaths.CliOutputLog(info.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            return Results.Ok(timeline);
        });

        // Per-run software-side change set: the commits whose author date
        // falls inside this run's wall-clock window. Drives the
        // "what did the agent change in my software?" question that
        // docs/design-principles.md treats as the unit of trust.
        // Index is 1-based to match RunRecord.Index.
        group.MapGet("/{jobId}/runs/{index:int}/commits", (
            string jobId, int index, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            var lines = CliOutputLogParser.ParseFile(JobPaths.CliOutputLog(info.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            if (index < 1 || index > timeline.Runs.Count)
                return Results.NotFound(new { error = $"Run #{index} not in this job's timeline (have {timeline.Runs.Count})." });
            var run = timeline.Runs[index - 1];
            var commits = git.GetCommitsBetween(jobId, watchPath, run.StartedAt, run.EndedAt ?? DateTime.UtcNow);
            return Results.Ok(new
            {
                runIndex = run.Index,
                startedAt = run.StartedAt,
                endedAt = run.EndedAt,
                commits
            });
        });

        // Manual re-trigger of the Haiku summary that the runner normally fires
        // post-execution. Surfaced behind a button while we iterate on the prompt
        // and observe failure modes — overwrites status.md when Haiku succeeds.
        // Pre-flight checks (e.g. missing cli-output.log) happen inside
        // GenerateAsync so the failure mode is recorded as a regular Failed
        // SummaryState the UI can render in-place — surfacing the precise
        // reason via the banner instead of a top-level error dialog.
        group.MapPost("/{jobId}/summary/regenerate", (string jobId, string? watchPath, JobScannerService scanner, SummaryGenerationService summaries) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null)
                return Results.NotFound(new { error = "Job not found" });

            // Fire-and-forget — the frontend polls summaryState until it flips
            // out of "generating", same path the post-run summary uses.
            _ = summaries.GenerateAsync(info);
            return Results.Accepted();
        });

        group.MapPost("/{jobId}/context-usage/refresh", async (string jobId, string? watchPath, TaskRunnerService runner, CancellationToken ct) =>
        {
            var (snapshot, error) = await runner.RefreshContextUsageAsync(jobId, watchPath, ct);
            return snapshot is not null
                ? Results.Ok(snapshot)
                : Results.BadRequest(new { error = error ?? "Cannot refresh context usage" });
        });
    }
}
