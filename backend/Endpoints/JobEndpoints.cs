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

        group.MapPut("/{jobId}/state", async (string jobId, string? watchPath, MoveJobRequest req,
            JobScannerService scanner, GitService git, ProjectSettingsService settings, ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            return MoveResult(await MoveAndMaybeAutoCommitAsync(scanner, git, settings, logger, jobId, req.TargetState, watchPath, ct));
        });

        group.MapPost("/{jobId}/move", async (string jobId, string? watchPath, MoveJobRequest req,
            JobScannerService scanner, GitService git, ProjectSettingsService settings, ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!JobStates.All.Contains(req.TargetState))
                return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");

            return MoveResult(await MoveAndMaybeAutoCommitAsync(scanner, git, settings, logger, jobId, req.TargetState, watchPath, ct));
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

        // Prompt-editor screenshot uploads — written to <job>/attachments/<id>.<ext> and
        // referenced from prompt.md as a relative path so the CLI agent finds them on disk.
        group.MapPost("/{jobId}/attachments", async (string jobId, string? watchPath, HttpRequest request, JobScannerService scanner) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data expected" });

            var form = await request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var (fileName, error) = scanner.SaveAttachment(jobId, watchPath, ms.ToArray(), file.FileName, file.ContentType);
            if (fileName is null) return Results.BadRequest(new { error });

            // Relative URL so the editor renders it via the API; markdown stores `attachments/<file>`.
            var watchPathQuery = string.IsNullOrEmpty(watchPath) ? "" : $"?watchPath={Uri.EscapeDataString(watchPath)}";
            return Results.Ok(new
            {
                fileName,
                relativePath = $"attachments/{fileName}",
                url = $"/api/jobs/{Uri.EscapeDataString(jobId)}/attachments/{fileName}{watchPathQuery}"
            });
        }).DisableAntiforgery();

        group.MapGet("/{jobId}/attachments/{fileName}", (string jobId, string fileName, string? watchPath, JobScannerService scanner) =>
        {
            var (path, contentType) = scanner.ResolveAttachment(jobId, fileName, watchPath);
            return path is null ? Results.NotFound() : Results.File(path, contentType);
        });

        // Read-only mirror of /attachments/ for the job's `results/` folder — the
        // place where agents drop screenshots that should survive past the next
        // Playwright run. The protocol pane resolves `results/<name>` references
        // in status.md against this URL. See docs/protocol-style.md.
        group.MapGet("/{jobId}/results/{fileName}", (string jobId, string fileName, string? watchPath, JobScannerService scanner) =>
        {
            var (path, contentType) = scanner.ResolveResult(jobId, fileName, watchPath);
            return path is null ? Results.NotFound() : Results.File(path, contentType);
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

        // Git endpoints — operate on the project's RootPath repository.
        group.MapGet("/{jobId}/git/status", (string jobId, string? watchPath, GitService git) =>
            Results.Ok(git.GetStatus(jobId, watchPath)));

        group.MapGet("/{jobId}/git/diff", (string jobId, string? watchPath, string? path, GitService git) =>
            Results.Text(git.GetDiff(jobId, watchPath, path), "text/plain"));

        group.MapPost("/{jobId}/git/commit", (string jobId, string? watchPath, GitCommitRequest req, GitService git) =>
        {
            var result = git.Commit(jobId, watchPath, req.Message);
            return result.Success
                ? Results.Ok(new { sha = result.Sha })
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/{jobId}/git/generate-message", async (string jobId, string? watchPath, GitService git, CancellationToken ct) =>
        {
            var result = await git.GenerateCommitMessageAsync(jobId, watchPath, ct);
            return result.Message is not null
                ? Results.Ok(new { message = result.Message })
                : Results.BadRequest(new { error = result.Error });
        });

        // Per-job commit details: returns the cached snapshot from job.json plus
        // a live re-derivation of the file list from `git show --name-status`,
        // so the detail view stays accurate even after history rewrites.
        group.MapGet("/{jobId}/commit", (string jobId, string? watchPath, JobScannerService scanner, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (info.Commit == null) return Results.Ok(new { commit = (object?)null, files = Array.Empty<GitFileChange>() });

            var live = git.GetCommitFiles(jobId, watchPath, info.Commit.Sha);
            var files = live.Count > 0 ? live : info.Commit.Files.Select(p => new GitFileChange("?", p, 0, 0)).ToList();
            return Results.Ok(new { commit = info.Commit, files });
        });

        // Diff for the recorded commit, optionally scoped to one path. Lets
        // the detail view show the exact changes the task produced even long
        // after the working tree has moved on.
        group.MapGet("/{jobId}/commit/diff", (string jobId, string? watchPath, string? path, JobScannerService scanner, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info?.Commit == null) return Results.Text("", "text/plain");
            return Results.Text(git.GetCommitDiff(jobId, watchPath, info.Commit.Sha, path), "text/plain");
        });

        group.MapPost("/{jobId}/open-in-vscode", (string jobId, string? watchPath, GitService git) =>
        {
            return git.OpenInVsCode(jobId, watchPath, out var error)
                ? Results.Ok()
                : Results.BadRequest(new { error });
        });

        // Claude-specific live session telemetry: reads the CLI's JSONL file
        // directly so we can show live tokens / model without spawning a PTY
        // or interrupting the running process.
        group.MapGet("/{jobId}/claude/session-info", (string jobId, string? watchPath, JobScannerService scanner, ClaudeSessionInspector inspector, ClaudeCliService claude) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            // Live rate-limit snapshot is per-CLI-process and lives only for
            // the lifetime of the running CLI; merge it onto the JSONL-based
            // snapshot so the frontend gets one consistent payload.
            var rateLimit = claude.GetLastRateLimit(info.JobKey);

            if (string.IsNullOrWhiteSpace(info.SessionName))
                return Results.Ok(new
                {
                    sessionInfo = new ClaudeSessionInfo("", null, 0, 0, 0, 0, 0, null, 0, "Job has no recorded sessionId yet — run it once first."),
                    rateLimit
                });

            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == info.ProjectName);
            var cwd = entry?.RootPath;
            if (string.IsNullOrWhiteSpace(cwd))
                return Results.BadRequest(new { error = "Project has no RootPath configured." });

            var snapshot = inspector.Inspect(info.SessionName, cwd);
            return Results.Ok(new { sessionInfo = snapshot, rateLimit });
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

        app.MapGet("/api/watch-paths", (JobScannerService scanner) =>
        {
            var entries = scanner.GetWatchPaths();
            return Results.Ok(entries);
        });

        // Lists the centrally-managed agent-rule files that are appended as a
        // system-prompt overlay to every Claude job. Used by the Job Detail
        // header to show "Active rules" so the user can verify what's in scope.
        app.MapGet("/api/agent-rules", (IConfiguration config) =>
        {
            var configured = config["AgentRules:CorePath"];
            if (string.IsNullOrWhiteSpace(configured))
                return Results.Ok(Array.Empty<object>());

            var candidates = new List<string>();
            if (Path.IsPathRooted(configured))
            {
                candidates.Add(configured);
            }
            else
            {
                candidates.Add(Path.GetFullPath(configured));
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    candidates.Add(Path.Combine(dir.FullName, configured));
                    dir = dir.Parent;
                }
            }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                var fi = new FileInfo(candidate);
                return Results.Ok(new[]
                {
                    new
                    {
                        name = Path.GetFileName(candidate),
                        path = candidate,
                        sizeBytes = fi.Length,
                        modifiedAt = fi.LastWriteTimeUtc
                    }
                });
            }
            return Results.Ok(Array.Empty<object>());
        });

        // Per-project git summary, used by board tile pills. Cached server-side
        // for ~3 s so the board can call freely without forking N git processes.
        app.MapGet("/api/git/summary", (GitService git) => Results.Ok(git.GetSummaries()));

        // Per-project preferences (auto-commit on/off today). Read-all returns a
        // flat map keyed by project name so the header can render every toggle
        // in one shot without N round-trips.
        app.MapGet("/api/projects/settings", (ProjectSettingsService settings) =>
        {
            return Results.Ok(settings.GetAll());
        });

        app.MapPut("/api/projects/{projectName}/auto-commit", (string projectName, SetAutoCommitRequest req, ProjectSettingsService settings, JobScannerService scanner) =>
        {
            // Reject unknown project names so a typo in the URL fails loud rather than silently
            // adding orphan settings entries that never reach a board column.
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutoCommit(projectName, req.Enabled);
            return Results.Ok(settings.Get(projectName));
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

    /// <summary>
    /// Wraps <see cref="JobScannerService.MoveJob"/> with the auto-commit hook:
    /// when the project has auto-commit enabled and the transition is
    /// <c>3-progress → 4-review</c>, generate a Conventional Commit message via
    /// Haiku, commit on the workspace repo, then move the job folder and stamp
    /// the SHA onto its <c>job.json</c>. The move always proceeds — a commit
    /// failure is logged but never blocks the state transition, so the user
    /// never gets stuck mid-pipeline because the LLM call timed out.
    /// </summary>
    private static async Task<MoveJobOutcome> MoveAndMaybeAutoCommitAsync(
        JobScannerService scanner, GitService git, ProjectSettingsService settings, ILogger logger,
        string jobId, string targetState, string? watchPath, CancellationToken ct)
    {
        var info = scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);

        var shouldAutoCommit =
            info.State == JobStates.Progress &&
            targetState == JobStates.Review &&
            settings.Get(info.ProjectName).AutoCommit;

        JobCommitInfo? commitToStamp = null;
        if (shouldAutoCommit)
        {
            try
            {
                var (result, message) = await git.AutoCommitAsync(jobId, watchPath, ct);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Sha))
                {
                    var files = git.GetCommitFiles(jobId, watchPath, result.Sha);
                    commitToStamp = new JobCommitInfo
                    {
                        Sha = result.Sha,
                        ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                        Message = message,
                        FilesChanged = files.Count,
                        Files = files.Select(f => f.Path).ToList(),
                        At = DateTime.UtcNow
                    };
                }
                else
                {
                    logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-commit threw for {JobId} — moving without a recorded SHA", jobId);
            }
        }

        var outcome = scanner.MoveJob(jobId, targetState, watchPath);
        if (outcome.Status == MoveJobStatus.Success && commitToStamp != null)
        {
            // Re-resolve the job — its FolderPath has shifted from progress/ to review/.
            var moved = scanner.FindJob(jobId, watchPath);
            if (moved != null)
                scanner.SetJobCommitOnFolder(moved.FolderPath, commitToStamp);
        }

        return outcome;
    }

    private static IResult MoveResult(MoveJobOutcome outcome) => outcome.Status switch
    {
        MoveJobStatus.Success => Results.Ok(),
        MoveJobStatus.NotFound => Results.NotFound(),
        MoveJobStatus.TargetFolderExists => Results.Conflict(new { error = outcome.Message }),
        _ => Results.Json(new { error = outcome.Message ?? "Failed to move job" }, statusCode: StatusCodes.Status500InternalServerError)
    };

    private static JobInfo WithExecution(JobInfo job, CliRouter router)
    {
        return job with { Execution = router.Get(job.CliType).GetExecution(job.JobKey) };
    }

    private static JobDetail WithExecution(JobDetail detail, CliRouter router)
    {
        return detail with { Info = WithExecution(detail.Info, router) };
    }
}
