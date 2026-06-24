

namespace AgentStudio.Cli;

/// <summary>
/// Cross-CLI observability surface: the multi-CLI introspection
/// routes under <c>/api/cli</c> (model catalogs, session
/// usage, quota windows, and the dev-only PTY probe).
/// </summary>
public static class CliEndpoints
{
    public static void MapCliEndpoints(this WebApplication app)
    {
        // ── Multi-CLI endpoints ────────────────────────────────────────

        var cliGroup = app.MapGroup("/api/cli");

        cliGroup.MapGet("/types", () => Results.Ok(CliTypes.All));

        // Per-CLI completion contract: how each backend signals turn
        // completion (native frame -> typed CliRunEvent). Static, derived
        // from the live adapter mappings; the Admin/CLI page renders it so
        // the contract shown is the real one, not a frontend guess.
        cliGroup.MapGet("/contracts", () => Results.Ok(CliCompletionContracts.All));

        // ── Working Memory (ASS-1748 / T1c): per-CLI persistent-state panel ──
        // GET lists the memory / session state a CLI keeps on disk (path, size,
        // last-used, preview) plus its protected auth / config entries; DELETE
        // removes a single memory / session state. The delete is guarded inside
        // CliWorkingMemoryService so this surface can never remove credentials -
        // auth / config entries are reported Deletable=false and refused.
        cliGroup.MapGet("/{cliType}/working-memory", (string cliType, CliWorkingMemoryService mem) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            return Results.Ok(mem.Describe(cliType));
        });

        cliGroup.MapDelete("/{cliType}/working-memory", (string cliType, string? path, CliWorkingMemoryService mem) =>
        {
            if (!CliTypes.IsValid(cliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{cliType}'" });
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path query parameter is required" });

            var result = mem.Delete(cliType, path);
            return result.Status switch
            {
                CliWorkingMemoryDeleteStatus.Deleted => Results.Ok(result),
                CliWorkingMemoryDeleteStatus.NotFound => Results.Json(result, statusCode: StatusCodes.Status404NotFound),
                CliWorkingMemoryDeleteStatus.Protected => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

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
                // Last-resort guard: discovery (e.g. a CLI's PTY probe) can
                // fail when no cache exists. Return 503 with the reason so the
                // UI can surface "models temporarily unavailable" rather than
                // breaking the whole page on a 500.
                return Results.Json(
                    new { error = ex.Message, cliType },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        cliGroup.MapGet("/usage", (CliRouter router, SessionRegistry sessions, TaskRunnerService runners) =>
        {
            // Snapshot the runner's per-project active job so the
            // LinkedJob chip can render `active` (green) when the linked
            // session belongs to the project's currently-running task.
            var status = runners.GetStatus();
            var activeJobByProject = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, projectStatus) in status.Projects)
            {
                activeJobByProject[name] = projectStatus.ActiveJobId;
            }
            return Results.Ok(sessions.BuildReport(router, activeJobByProject));
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

        // ── Quota caps: per-CLI per-window usage ceilings ──
        // The user uses Claude Code (and others) outside the orchestrator too;
        // these caps stop the runner from burning the last few percent of a
        // 5-hour window or the weekly budget so manual ad-hoc work still has
        // headroom. Default 95% applied when no entry is configured.
        cliGroup.MapGet("/quota/caps", (CliQuotaCapsService caps) =>
        {
            return Results.Ok(new
            {
                defaultCapPct = CliQuotaCapsService.DefaultCapPct,
                caps = caps.GetAll()
            });
        });

        cliGroup.MapPut("/quota/caps", (SetCliQuotaCapRequest req, CliQuotaCapsService caps) =>
        {
            if (string.IsNullOrWhiteSpace(req.CliType) || string.IsNullOrWhiteSpace(req.WindowLabel))
                return Results.BadRequest(new { error = "cliType and windowLabel are required" });
            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown cliType '{req.CliType}'" });
            if (req.CapPct < 1 || req.CapPct > 100)
                return Results.BadRequest(new { error = "capPct must be between 1 and 100" });

            caps.SetCap(req.CliType, req.WindowLabel, req.CapPct);
            return Results.Ok(new
            {
                defaultCapPct = CliQuotaCapsService.DefaultCapPct,
                caps = caps.GetAll()
            });
        });

        // ── TEMPORARY: PTY slash-command probe for parser development ──
        // Spawns the requested CLI in a scratch dir, sends a slash command,
        // waits for output to settle, returns the ANSI-stripped snapshot.
        // Example: /api/cli/_probe/claude?cmd=/usage
        cliGroup.MapGet("/_probe/{cliType}", async (
            string cliType,
            string? cmd,
            string? followUp,
            int? settleMs,
            int? followUpSettleMs,
            CliRouter router,
            CliEnvironment env,
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
            try { env.EnsureFolderTrusted(scratch); env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal"); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:178"); }

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
                try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:199"); }
                try { await pty.SendKeysAsync("<Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliEndpoints:200"); }
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
}
