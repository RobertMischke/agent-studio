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

        group.MapPost("/{jobId}/stop", (string jobId, string? watchPath, string? reason, TaskRunnerService runner) =>
        {
            // 'reason' is a hint that travels into RunStatusClassifier so the
            // resulting CliExecution.Status reads as 'stopped' instead of
            // 'failed'. UI sends 'followup' for Pause & Send so the next
            // continue does not look like a crash recovery; everything else
            // (manual Pause button, no value) is a UserStop. Unknown values
            // fall back to UserStop rather than rejecting the request.
            var parsed = (reason ?? "user").Trim().ToLowerInvariant() switch
            {
                "followup" or "followup-pause" => OrchestratorApi.Services.Runner.RunStopReason.FollowupPause,
                "watchdog" => OrchestratorApi.Services.Runner.RunStopReason.Watchdog,
                _ => OrchestratorApi.Services.Runner.RunStopReason.UserStop
            };
            var success = runner.StopJob(jobId, watchPath, parsed);
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

        // Per-run software-side change set: the commits authored during
        // this run. Prefers the deterministic SHA range
        // HeadShaBefore..HeadShaAfter captured by ProjectRunner around
        // the CLI invocation; falls back to the wall-clock window for
        // older runs that don't have the SHAs persisted. The
        // wall-clock fallback is best-effort - the SHA-range path is
        // the source of truth for new runs and is what the integration
        // test pins. Index is 1-based to match RunRecord.Index.
        group.MapGet("/{jobId}/runs/{index:int}/commits", (
            string jobId, int index, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var run = ResolveRun(info, sessions, jobId, watchPath, index, out var error);
            if (run == null) return Results.NotFound(new { error });

            var commits = !string.IsNullOrWhiteSpace(run.HeadShaBefore) && !string.IsNullOrWhiteSpace(run.HeadShaAfter)
                ? git.GetCommitsInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter)
                : git.GetCommitsBetween(jobId, watchPath, run.StartedAt, run.EndedAt ?? DateTime.UtcNow);

            return Results.Ok(new
            {
                runIndex = run.Index,
                startedAt = run.StartedAt,
                endedAt = run.EndedAt,
                headShaBefore = run.HeadShaBefore,
                headShaAfter = run.HeadShaAfter,
                source = !string.IsNullOrWhiteSpace(run.HeadShaBefore) && !string.IsNullOrWhiteSpace(run.HeadShaAfter)
                    ? "sha-range"
                    : "wall-clock",
                commits
            });
        });

        // Aggregated file list for a run: every path touched by any
        // commit in HeadShaBefore..HeadShaAfter, with combined +/-
        // counts. Drives the file-tree side of the run's git viewer.
        group.MapGet("/{jobId}/runs/{index:int}/files", (
            string jobId, int index, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var run = ResolveRun(info, sessions, jobId, watchPath, index, out var error);
            if (run == null) return Results.NotFound(new { error });

            if (string.IsNullOrWhiteSpace(run.HeadShaBefore) || string.IsNullOrWhiteSpace(run.HeadShaAfter))
            {
                return Results.Ok(new
                {
                    runIndex = run.Index,
                    headShaBefore = run.HeadShaBefore,
                    headShaAfter = run.HeadShaAfter,
                    files = new List<GitFileChange>(),
                    note = "Run has no captured SHAs (older run or repo unavailable). The git viewer needs the SHA range."
                });
            }

            var files = git.GetFilesChangedInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter);
            return Results.Ok(new
            {
                runIndex = run.Index,
                headShaBefore = run.HeadShaBefore,
                headShaAfter = run.HeadShaAfter,
                files
            });
        });

        // Unified diff for one path across the run's SHA range. Returns
        // the raw diff body so the frontend's existing diff renderer
        // can consume it without re-parsing.
        group.MapGet("/{jobId}/runs/{index:int}/diff", (
            string jobId, int index, string? path, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var run = ResolveRun(info, sessions, jobId, watchPath, index, out var error);
            if (run == null) return Results.NotFound(new { error });

            if (string.IsNullOrWhiteSpace(run.HeadShaBefore) || string.IsNullOrWhiteSpace(run.HeadShaAfter))
                return Results.Ok(new { diff = "", note = "Run has no captured SHAs." });

            var diff = git.GetDiffInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter, path);
            return Results.Ok(new { diff });
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

        // Synchronous "interim status" peek while a run is in flight. Runs a
        // one-shot Haiku call against the live cli-output.log and returns the
        // markdown directly; status.md on disk is NOT touched, so the
        // post-run summary still owns it. Surfaced by the "Interim status"
        // button in the protocol pane so the user can check on a long-running
        // task without stopping it.
        group.MapPost("/{jobId}/summary/interim", async (string jobId, string? watchPath, JobScannerService scanner, SummaryGenerationService summaries, CancellationToken ct) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null)
                return Results.NotFound(new { error = "Job not found" });

            var result = await summaries.GenerateInterimAsync(info, ct);
            if (!result.Ok)
                return Results.BadRequest(new { error = result.Error ?? "Interim summary failed" });

            return Results.Ok(new { markdown = result.Markdown, durationMs = result.DurationMs });
        });

        group.MapPost("/{jobId}/context-usage/refresh", async (string jobId, string? watchPath, TaskRunnerService runner, CancellationToken ct) =>
        {
            var (snapshot, error) = await runner.RefreshContextUsageAsync(jobId, watchPath, ct);
            return snapshot is not null
                ? Results.Ok(snapshot)
                : Results.BadRequest(new { error = error ?? "Cannot refresh context usage" });
        });
    }

    /// <summary>
    /// Builds the run timeline for <paramref name="info"/> and returns
    /// the run at <paramref name="index"/> (1-based), or null + a
    /// 404-friendly error string. Lifted into a helper so the three
    /// per-run endpoints share the same lookup path and never drift
    /// in how they bound or pair runs.
    /// </summary>
    private static RunRecord? ResolveRun(
        JobInfo info, JobSessionLog sessions,
        string jobId, string? watchPath, int index, out string error)
    {
        error = "";
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(JobPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
        if (index < 1 || index > timeline.Runs.Count)
        {
            error = $"Run #{index} not in this job's timeline (have {timeline.Runs.Count}).";
            return null;
        }
        return timeline.Runs[index - 1];
    }
}
