using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Claude-specific live telemetry routes — kept separate so the
/// generic job endpoints stay CLI-agnostic. The single endpoint
/// today merges the JSONL-based session inspection with the
/// in-process rate-limit snapshot into one payload the protocol
/// pane consumes.
/// </summary>
public static class JobClaudeEndpoints
{
    public static void MapJobClaudeEndpoints(this RouteGroupBuilder group)
    {
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
    }
}
