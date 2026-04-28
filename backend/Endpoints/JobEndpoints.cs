using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;

namespace OrchestratorApi.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("/", (JobScannerService scanner, CliRouter router) =>
        {
            var jobs = scanner.ScanAllJobs().Select(job => WithExecution(job, router)).ToList();
            return Results.Ok(jobs);
        });

        group.MapGet("/grouped", (JobScannerService scanner, CliRouter router) =>
        {
            var jobs = scanner.ScanAllJobs().Select(job => WithExecution(job, router)).ToList();
            var grouped = new
            {
                Preparation = jobs.Where(j => j.State == JobStates.Preparation).OrderBy(j => j.Order).ToList(),
                Ready = jobs.Where(j => j.State == JobStates.Ready).OrderBy(j => j.Order).ToList(),
                Progress = jobs.Where(j => j.State == JobStates.Progress).OrderBy(j => j.Order).ToList(),
                Review = jobs.Where(j => j.State == JobStates.Review).OrderBy(j => j.Order).ToList(),
                Completed = jobs.Where(j => j.State == JobStates.Completed).OrderBy(j => j.Order).ToList(),
                Archive = jobs.Where(j => j.State == JobStates.Archive).OrderBy(j => j.Order).ToList()
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, string? watchPath, JobScannerService scanner, CliRouter router) =>
        {
            var detail = scanner.GetJobDetail(jobId, watchPath);
            return detail is null ? Results.NotFound() : Results.Ok(WithExecution(detail, router));
        });

        group.MapPut("/{jobId}/state", (string jobId, string? watchPath, MoveJobRequest req, JobScannerService scanner) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            var success = scanner.MoveJob(jobId, req.TargetState, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/{jobId}/move", (string jobId, string? watchPath, MoveJobRequest req, JobScannerService scanner) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            var success = scanner.MoveJob(jobId, req.TargetState, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapDelete("/{jobId}", (string jobId, string? watchPath, JobScannerService scanner) =>
        {
            var success = scanner.DeleteJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("/{jobId}/files/{fileName}", (string jobId, string fileName, string? watchPath, JobScannerService scanner) =>
        {
            var content = scanner.ReadJobFile(jobId, fileName, watchPath);
            return content is null ? Results.NotFound() : Results.Text(content);
        });

        group.MapPost("/", (CreateJobRequest req, JobScannerService scanner) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");

            var jobId = scanner.CreateJob(req);
            return jobId is null ? Results.Conflict("Job already exists or invalid input") : Results.Ok(new { id = jobId });
        });

        group.MapPut("/{jobId}/files/{fileName}", (string jobId, string fileName, string? watchPath, UpdateJobFileRequest req, JobScannerService scanner, TaskRunnerService runner) =>
        {
            if (runner.IsJobLive(jobId, watchPath))
                return Results.Conflict("Cannot edit while the CLI is running for this task — stop it first.");

            try
            {
                var success = scanner.UpdateJobFile(jobId, fileName, req.Content, watchPath);
                return success ? Results.Ok() : Results.NotFound("Job not found or file is not editable.");
            }
            catch (IOException ex)
            {
                // File was locked by another process (editor, indexer, AV) for longer than
                // the retry window. Surface a tidy 503 instead of a stack-trace modal.
                return Results.Json(
                    new { error = "File is temporarily locked by another process — try saving again in a moment.", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/reorder", (ReorderRequest req, JobScannerService scanner) =>
        {
            var jobs = req.Jobs.Count > 0
                ? req.Jobs
                : req.JobIds.Select(id => new JobOrderItem { JobId = id }).ToList();
            var success = scanner.ReorderJobs(jobs);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        group.MapPost("/{jobId}/change-project", (string jobId, string? watchPath, ChangeProjectRequest req, JobScannerService scanner) =>
        {
            var success = scanner.ChangeProject(jobId, req.TargetWatchPath, watchPath);
            return success ? Results.Ok() : Results.BadRequest("Failed to change project");
        });

        // CLI execution endpoints
        group.MapPost("/{jobId}/start", async (string jobId, string? watchPath, StartJobRequest? req, TaskRunnerService runner, JobScannerService scanner, CancellationToken ct) =>
        {
            var job = scanner.FindJob(jobId, watchPath);
            if (job == null)
                return Results.NotFound(new { error = "Job not found" });

            if (job.State is not (JobStates.Ready or JobStates.Progress))
                return Results.BadRequest(new { error = $"Job is in state '{job.State}' — only jobs in 'ready' or 'progress' can be started" });

            var (execution, error) = await runner.StartJobAsync(jobId, watchPath, req?.Model, req?.CliType, ct);
            return execution is not null
                ? Results.Ok(execution)
                : Results.BadRequest(new { error = error ?? "Cannot start job" });
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

            var (execution, error) = await runner.ContinueJobAsync(jobId, req.Prompt, watchPath, req.Model, ct);
            return execution is not null
                ? Results.Ok(execution)
                : Results.BadRequest(new { error = error ?? "Cannot continue job" });
        });

        group.MapPut("/{jobId}/model", (string jobId, string? watchPath, SetJobModelRequest req, JobScannerService scanner) =>
        {
            var success = scanner.SetJobModel(jobId, req?.Model, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/cli-type", (string jobId, string? watchPath, SetJobCliTypeRequest req, JobScannerService scanner) =>
        {
            if (req is null || !CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"cliType must be one of {string.Join(", ", CliTypes.All)}" });
            var ok = scanner.SetJobCliType(jobId, req.CliType, watchPath);
            if (!ok) return Results.NotFound();
            if (req.UseOwnSession.HasValue)
                scanner.SetJobUseOwnSession(jobId, req.UseOwnSession.Value, watchPath);
            return Results.Ok();
        });

        group.MapPut("/{jobId}/title", (string jobId, string? watchPath, SetJobTitleRequest req, JobScannerService scanner) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var success = scanner.SetJobTitle(jobId, req.Title, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("/{jobId}/output", (string jobId, string? watchPath, TaskRunnerService runner) =>
        {
            var output = runner.GetJobOutput(jobId, watchPath);
            return Results.Ok(output);
        });

        group.MapPost("/{jobId}/context-usage/refresh", async (string jobId, string? watchPath, TaskRunnerService runner, CancellationToken ct) =>
        {
            var (snapshot, error) = await runner.RefreshContextUsageAsync(jobId, watchPath, ct);
            return snapshot is not null
                ? Results.Ok(snapshot)
                : Results.BadRequest(new { error = error ?? "Cannot refresh context usage" });
        });

        app.MapGet("/api/watch-paths", (JobScannerService scanner) =>
        {
            var entries = scanner.GetWatchPaths();
            return Results.Ok(entries);
        });

        // Runner endpoints
        var runnerGroup = app.MapGroup("/api/runner");

        runnerGroup.MapGet("/status", (TaskRunnerService runner) =>
        {
            return Results.Ok(runner.GetStatus());
        });

        runnerGroup.MapPut("/{projectName}/mode", (string projectName, SetRunnerModeRequest req, TaskRunnerService runner) =>
        {
            var success = runner.SetMode(projectName, req.Mode);
            return success ? Results.Ok() : Results.BadRequest("Invalid project or mode");
        });

        runnerGroup.MapPost("/{projectName}/start", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StartRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        });

        runnerGroup.MapPost("/{projectName}/stop", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StopRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        });

        app.MapGet("/healthz", () => Results.Ok("ok"));

        // CLI settings endpoints
        var settingsGroup = app.MapGroup("/api/settings");

        settingsGroup.MapGet("/cli", (CopilotCliService cli) =>
        {
            var (available, version, path) = cli.TestCliPath();
            return Results.Ok(new { path, available, version, hasToken = cli.HasGitHubToken() });
        });

        settingsGroup.MapPut("/cli", (SetCliPathRequest req, CopilotCliService cli) =>
        {
            cli.SetCliPath(req.Path);
            var (available, version, path) = cli.TestCliPath();
            return Results.Ok(new { path, available, version, hasToken = cli.HasGitHubToken() });
        });

        settingsGroup.MapPost("/cli/test", (SetCliPathRequest req, CopilotCliService cli) =>
        {
            var (available, version, path) = cli.TestCliPath(req.Path);
            return Results.Ok(new { path, available, version, hasToken = cli.HasGitHubToken() });
        });

        settingsGroup.MapPut("/cli/token", (SetGitHubTokenRequest req, CopilotCliService cli) =>
        {
            cli.SetGitHubToken(req.Token);
            var (available, version, path) = cli.TestCliPath();
            return Results.Ok(new { path, available, version, hasToken = cli.HasGitHubToken() });
        });

        settingsGroup.MapGet("/cli/models", (CopilotCliService cli, bool? refresh) =>
        {
            try { return Results.Ok(cli.GetModelCatalog(forceRefresh: refresh ?? false)); }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // ── Multi-CLI endpoints ────────────────────────────────────────

        var cliGroup = app.MapGroup("/api/cli");

        cliGroup.MapGet("/types", () => Results.Ok(CliTypes.All));

        cliGroup.MapGet("/{cliType}/models", async (string cliType, bool? refresh, CliRouter router, CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            try
            {
                var catalog = await router.Get(cliType).GetModelCatalogAsync(refresh ?? false, ct);
                return Results.Ok(catalog);
            }
            catch (Exception ex)
            {
                // Last-resort guard: discovery (e.g. Copilot's PTY probe) can
                // fail when no cache exists. Return 503 with the reason so the
                // UI can surface "models temporarily unavailable" rather than
                // breaking the whole page on a 500.
                return Results.Json(
                    new { error = ex.Message, cliType },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        cliGroup.MapGet("/usage", (CliRouter router, SessionRegistry sessions) =>
        {
            return Results.Ok(sessions.BuildReport(router));
        });

        // ── Quota: per-CLI subscription quota for the right-hand sidesheet ──
        cliGroup.MapGet("/quota", (QuotaService quota, CancellationToken ct) =>
        {
            return Results.Ok(quota.GetWithBackgroundRefresh(ct));
        });

        cliGroup.MapPost("/quota/refresh", async (QuotaService quota, CancellationToken ct) =>
        {
            return Results.Ok(await quota.RefreshAllAsync(ct));
        });

        cliGroup.MapPost("/quota/refresh/{cliType}", async (string cliType, QuotaService quota, CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            var snap = await quota.RefreshAsync(cliType, ct);
            return snap == null ? Results.NotFound() : Results.Ok(snap);
        });

        // ── TEMPORARY: PTY slash-command probe for parser development ──
        // Spawns the requested CLI in a scratch dir, sends a slash command,
        // waits for output to settle, returns the ANSI-stripped snapshot.
        // Example: /api/cli/_probe/copilot?cmd=/usage
        cliGroup.MapGet("/_probe/{cliType}", async (
            string cliType,
            string? cmd,
            string? followUp,
            int? settleMs,
            int? followUpSettleMs,
            CliRouter router,
            CopilotCliEnvironment env,
            CancellationToken ct) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            var slashCmd = string.IsNullOrWhiteSpace(cmd) ? "/usage" : cmd!;
            var settle = settleMs ?? 2500;

            var cli = router.Get(cliType);
            var (available, _, resolvedPath) = cli.TestCliPath();
            if (!available)
                return Results.BadRequest(new { error = $"{cliType} CLI not available" });

            var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-probe", cliType);
            Directory.CreateDirectory(scratch);
            try { env.EnsureFolderTrusted(scratch); env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal"); } catch { }

            try
            {
                await using var pty = await PtySession.SpawnAsync(app: resolvedPath, cwd: scratch, ct: ct);
                await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
                // For Claude/Codex confirm trust prompt first.
                if (cliType is "claude" or "codex")
                {
                    await pty.SendKeysAsync("1<Enter>", ct);
                    await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: 8000, ct);
                }
                var preLen = pty.SnapshotStripped().Length;
                await pty.SendKeysAsync(slashCmd + "<Enter>", ct);
                await pty.WaitForIdleAsync(idleMs: settle, timeoutMs: 12000, ct);
                if (!string.IsNullOrEmpty(followUp))
                {
                    await pty.SendKeysAsync(followUp, ct);
                    await pty.WaitForIdleAsync(idleMs: followUpSettleMs ?? 2000, timeoutMs: 10000, ct);
                }
                var snap = pty.SnapshotStripped();
                try { await pty.SendKeysAsync("<Esc>", ct); } catch { }
                try { await pty.SendKeysAsync("<Esc>", ct); } catch { }
                return Results.Ok(new
                {
                    cliType,
                    command = slashCmd,
                    followUp,
                    resolvedPath,
                    preCharCount = preLen,
                    snapshotLength = snap.Length,
                    snapshot = snap
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Probe failed");
            }
        });
    }

    private static JobInfo WithExecution(JobInfo job, CliRouter router)
    {
        return job with { Execution = router.Get(job.CliType).GetExecution(job.JobKey) };
    }

    private static JobDetail WithExecution(JobDetail detail, CliRouter router)
    {
        return detail with { Info = WithExecution(detail.Info, router) };
    }
}
