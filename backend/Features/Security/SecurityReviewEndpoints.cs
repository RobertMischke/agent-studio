

namespace AgentStudio.Security;

/// <summary>
/// Read + manual-trigger surface for the project Security panel (slice 1
/// of the quality-system mockup, docs/mockups/quality-system/). The panel
/// summarises the project's most recent security review, its baseline
/// state, and the review history. Three actions live here: list reviews,
/// read the baseline, and queue a new security audit job.
///
/// Storage layout (taxonomy.md "Storage Shape"):
/// <c>&lt;workspace&gt;/projects/&lt;project&gt;/security/baseline.md</c> +
/// <c>&lt;workspace&gt;/projects/&lt;project&gt;/security/reviews/*.md</c>.
/// The folder lives next to the project's job lanes so the orchestrator
/// can write evidence without leaving the watched workspace.
/// </summary>
public static class SecurityReviewEndpoints
{
    /// <summary>Title prefix used for queued security audit jobs. Lets the duplicate-guard recognise its own jobs.</summary>
    public const string AuditJobTitlePrefix = "Security audit";

    public static void MapSecurityReviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/security/reviews", (
            string projectName,
            SecurityReviewService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var resolved = svc.ResolveSecurityDir(projectName);
            if (resolved is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var list = svc.ListReviews(projectName);
            return Results.Ok(list);
        });

        app.MapGet("/api/projects/{projectName}/security/reviews/{fileName}", (
            string projectName,
            string fileName,
            SecurityReviewService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var content = svc.ReadReview(projectName, fileName);
            if (content is null)
                return Results.NotFound(new { error = "review not found or path rejected" });
            return Results.Ok(new { fileName, content });
        });

        app.MapGet("/api/projects/{projectName}/security/baseline", (
            string projectName,
            SecurityReviewService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var resolved = svc.ResolveSecurityDir(projectName);
            if (resolved is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.GetBaseline(projectName));
        });

        // Queue a new security-audit job. Action-driven: the audit runs as
        // its own normal CLI job (single-active-per-project applies via the
        // runner), and the prompt invokes the existing /security-review
        // skill. The duplicate guard prevents a second click from stacking
        // multiple identical audits behind the active task.
        app.MapPost("/api/projects/{projectName}/security/audit", (
            string projectName,
            HttpContext ctx,
            TaskScannerService scanner,
            TaskMutationService mutations) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            // Refuse to queue a duplicate audit while one is already
            // pending or running. The check covers 1-preparation through
            // 3-progress — anything past 3-progress is finished from the
            // runner's perspective and should not block a fresh request.
            var openLanes = new[] { TaskStates.Preparation, TaskStates.OrchestratorPrep, TaskStates.Ready, TaskStates.Progress };
            var existingAudit = scanner.ScanAllAutomationJobs().FirstOrDefault(j =>
                string.Equals(j.WatchPath, entry.Path, StringComparison.OrdinalIgnoreCase) &&
                openLanes.Contains(j.State, StringComparer.OrdinalIgnoreCase) &&
                (j.Title?.StartsWith(AuditJobTitlePrefix, StringComparison.OrdinalIgnoreCase) ?? false));
            if (existingAudit is not null)
            {
                return Results.Conflict(new
                {
                    error = "audit-already-pending",
                    message = $"A security audit is already in {existingAudit.State} on this project ({existingAudit.Id}).",
                    jobId = existingAudit.Id,
                    state = existingAudit.State,
                });
            }

            var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
            var slug = $"security-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var ownerHeader = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
            var req = new CreateTaskRequest
            {
                Id = slug,
                Title = $"{AuditJobTitlePrefix} {stamp}",
                Agent = "claude",
                CliType = "claude",
                WatchPath = entry.Path,
                TargetState = TaskStates.Ready,
                PromptMarkdown = BuildAuditPromptMarkdown(projectName),
                OwnerClientId = string.IsNullOrWhiteSpace(ownerHeader) ? null : ownerHeader,
            };
            var jobId = mutations.CreateJob(req);
            if (jobId is null)
                return Results.Conflict(new { error = "create-failed", message = "Job already exists or invalid input." });
            return Results.Ok(new { jobId, state = TaskStates.Ready, title = req.Title });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);
    }

    private static string BuildAuditPromptMarkdown(string projectName) =>
$@"# Security audit

Run the `/security-review` skill against this project's source tree and write the resulting evidence file under the project's `security/reviews/` folder using the file name `YYYY-MM-DD-<short-slug>.md` (today's date in UTC).

The first block of the file must be YAML frontmatter so the project Security panel can render the verdict, severity split, and last-review date without re-parsing the prose:

```yaml
---
date: YYYY-MM-DD
verdict: ok|stale|failing
severity: info|warn|critical
openFindings: <int>
severities:
  critical: 0
  high: 0
  medium: 0
  low: 0
title: <short headline>
summary: <one-sentence summary>
---
```

Findings should be evidence; do not mutate task state. Follow-up work, if any, belongs as a normal queued task on the project board.

When the audit is complete, end the run with `[[TASK_DONE]]`.
";
}
