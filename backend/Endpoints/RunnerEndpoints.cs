using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Project-runner control surface under <c>/api/runner</c>: the
/// status snapshot the board polls plus the manual mode / start /
/// stop toggles. Per-job execution lives in
/// <see cref="OrchestratorApi.Endpoints.Jobs.JobRunnerEndpoints"/> —
/// these routes operate at project granularity.
/// </summary>
public static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this WebApplication app)
    {
        var runnerGroup = app.MapGroup("/api/runner");

        runnerGroup.MapGet("/status", (TaskRunnerService runner) =>
        {
            return Results.Ok(runner.GetStatus());
        });

        // Global orchestrator session: the singleton that lives above the
        // per-project orchestrators. Surfaces the same shape as the per-
        // project session so the frontend can reuse the rendering. Lives
        // at /api/runner/global/orchestrator-session for symmetry with the
        // per-project route.
        runnerGroup.MapGet("/global/orchestrator-session",
            (GlobalOrchestratorSessionStore store) =>
            {
                var session = store.Read();
                return Results.Ok(new { project = "(global)", session });
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

        // Orchestrator log: chronological feed of decisions / actions /
        // observations / interventions for the named project. Read-only;
        // entries are appended by the runner today and (Phase D+) by a
        // dedicated orchestrator process. The frontend renders this as
        // the "Orchestrator" feed in the project detail view.
        runnerGroup.MapGet("/{projectName}/orchestrator-log",
            (string projectName, JobScannerService scanner, OrchestratorLog log) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var entries = log.Read(entry.Path);
                return Results.Ok(new { project = projectName, entries });
            });

        // Long-lived orchestrator session for the project. Surfaces the
        // session id (so the user can `claude -r <id>` themselves to
        // inspect or talk directly to it), the boot prompt preview ("what
        // did you read on boot?"), the boot reply preview ("what did you
        // say back?"), and cumulative token totals across the session's
        // lifetime. Read-only.
        runnerGroup.MapGet("/{projectName}/orchestrator-session",
            (string projectName, JobScannerService scanner, OrchestratorSessionStore sessions) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var session = sessions.Read(entry.Path);
                return Results.Ok(new { project = projectName, session });
            });

        // Token summary: per-project rollup of orchestrator-log token
        // amounts plus a *theoretical* API-cost estimate. The frontend
        // renders amounts prominently, the cost smaller and behind a
        // disclaimer (the user pays via CLI subscriptions, not API).
        runnerGroup.MapGet("/{projectName}/token-summary",
            (string projectName, JobScannerService scanner, TokenSummaryService tokens) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                return Results.Ok(tokens.Summarize(projectName, entry.Path));
            });

        // User override on an orchestrator decision (Phase F). Appends an
        // intervention entry to the feed and, when the named job is in a
        // continuable state, routes the new direction through the existing
        // Continue path so the override actually takes effect on the agent.
        runnerGroup.MapPost("/{projectName}/orchestrator-log/override",
            async (string projectName, OrchestratorOverrideRequest req, JobScannerService scanner, OrchestratorLog log, TaskRunnerService runner, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req?.NewDirection))
                    return Results.BadRequest(new { error = "newDirection is required" });

                var watchEntry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (watchEntry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                // Always record the intervention in the feed, regardless of
                // whether we can route the follow-up. The audit trail is
                // half the value.
                log.Append(watchEntry.Path, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Intervention,
                    Topic = "user-override",
                    JobId = string.IsNullOrWhiteSpace(req.JobId) ? null : req.JobId,
                    Summary = $"User overrode an orchestrator entry from {req.OriginalTs:u}.",
                    Reasoning = $"New direction: {Truncate(req.NewDirection, 600)}",
                    UserOverride = new OrchestratorIntervention
                    {
                        At = DateTime.UtcNow,
                        NewDirection = req.NewDirection
                    }
                });

                // If the override names a job, treat the new direction as a
                // Continue follow-up. Reuses the busy-project queue path:
                // when the project is currently busy with another job, the
                // intent is saved on the target and runs on next pickup.
                if (!string.IsNullOrWhiteSpace(req.JobId))
                {
                    try
                    {
                        await runner.ContinueJobAsync(req.JobId, req.NewDirection, watchEntry.Path, modelOverride: null, mode: "steer", ct);
                    }
                    catch (Exception ex)
                    {
                        return Results.Ok(new
                        {
                            applied = false,
                            error = ex.Message,
                            note = "Override recorded in the feed; Continue could not be routed."
                        });
                    }
                    return Results.Ok(new { applied = true });
                }

                return Results.Ok(new { applied = false, note = "Override recorded in the feed; no jobId was given to route to." });
            });

        // Orchestrator chat (Phase 3): the side-sheet conversation surface.
        // Different from the orchestrator log: the log records what the
        // runner / orchestrator did on its own; the chat is a real
        // bidirectional dialogue between the user and the global orchestrator
        // session, scoped to one project tab. Persisted under
        // <watchPath>/.orchestrator/orchestrator-chat.jsonl.
        runnerGroup.MapGet("/{projectName}/orchestrator-chat",
            (string projectName, JobScannerService scanner, OrchestratorChatService chatService) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var turns = chatService.Read(entry.Path);
                return Results.Ok(new { project = projectName, turns });
            });

        runnerGroup.MapPost("/{projectName}/orchestrator-chat",
            async (string projectName, SendOrchestratorChatRequest req, JobScannerService scanner, OrchestratorChatService chatService, CancellationToken ct) =>
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Text))
                    return Results.BadRequest(new { error = "text is required" });

                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                var reply = await chatService.SendAsync(projectName, entry.Path, req, ct);
                return Results.Ok(new { project = projectName, reply });
            });

        // Image upload + serving for the orchestrator chat composer.
        // Files land under <watchPath>/.orchestrator/chat-attachments/.
        // The frontend uploads each pasted/dropped image first, then sends
        // the chat message with the relative paths so the orchestrator
        // sees them as proper file references rather than placeholders.
        runnerGroup.MapPost("/{projectName}/orchestrator-chat/attachments",
            async (string projectName, HttpRequest request, JobScannerService scanner, OrchestratorChat chat) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "multipart/form-data expected" });

                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

                var form = await request.ReadFormAsync();
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded" });

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);

                var (fileName, relativePath, error) = chat.SaveAttachment(entry.Path, ms.ToArray(), file.FileName, file.ContentType);
                if (fileName is null) return Results.BadRequest(new { error });

                return Results.Ok(new
                {
                    fileName,
                    relativePath,
                    url = $"/api/runner/{Uri.EscapeDataString(projectName)}/orchestrator-chat/attachments/{fileName}"
                });
            }).DisableAntiforgery();

        runnerGroup.MapGet("/{projectName}/orchestrator-chat/attachments/{fileName}",
            (string projectName, string fileName, JobScannerService scanner, OrchestratorChat chat) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound();
                var (path, contentType) = chat.ResolveAttachment(entry.Path, fileName);
                return path is null ? Results.NotFound() : Results.File(path, contentType);
            });

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s[..(max - 1)].TrimEnd() + "...";
        }
    }
}
