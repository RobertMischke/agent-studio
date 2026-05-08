using OrchestratorApi.Models;
using OrchestratorApi.Services.Design;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read + manual-trigger surface for the project UX/UI panel (slice 6 of
/// the quality-system mockup, docs/mockups/quality-system/). Three GET
/// endpoints render the panel (overview, references, council notes); four
/// action POSTs queue the design loop's spawned skills as normal CLI jobs;
/// one POST stamps <c>acceptedAt</c> into a council note's frontmatter.
///
/// Storage layout (taxonomy.md "Storage Shape"):
/// <c>&lt;workspace&gt;/projects/&lt;project&gt;/design/</c> with subfolders
/// <c>references/</c>, <c>council/</c>, plus <c>brief.md</c> + <c>loop.md</c>.
/// Action-driven principle: nothing runs on read; the panel never auto-applies a council suggestion.
/// </summary>
public static class DesignSurfaceEndpoints
{
    /// <summary>Title prefix for queued design-loop jobs (action-driven; one per skill click).</summary>
    public const string ScreenshotCritiqueTitlePrefix = "Design: screenshot critique";
    public const string CouncilReviewTitlePrefix = "Design: council review";
    public const string NextVersionTitlePrefix = "Design: next version";

    public static void MapDesignSurfaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/design/overview", (
            string projectName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var dir = svc.ResolveDesignDir(projectName);
            if (dir is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.GetOverview(projectName));
        });

        app.MapGet("/api/projects/{projectName}/design/references", (
            string projectName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var dir = svc.ResolveDesignDir(projectName);
            if (dir is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.ListReferences(projectName));
        });

        app.MapGet("/api/projects/{projectName}/design/council", (
            string projectName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var dir = svc.ResolveDesignDir(projectName);
            if (dir is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(svc.ListCouncilNotes(projectName));
        });

        app.MapGet("/api/projects/{projectName}/design/council/{fileName}", (
            string projectName,
            string fileName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var content = svc.ReadCouncilNote(projectName, fileName);
            if (content is null)
                return Results.NotFound(new { error = "note not found or path rejected" });
            return Results.Ok(new { fileName, content });
        });

        app.MapGet("/api/projects/{projectName}/design/references/{fileName}", (
            string projectName,
            string fileName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var content = svc.ReadReference(projectName, fileName);
            if (content is null)
                return Results.NotFound(new { error = "reference not found or path rejected" });
            return Results.Ok(new { fileName, content });
        });

        // Stamps acceptedAt into the note's frontmatter so the panel can
        // separate accepted from open council items. Returns 404 when the
        // file doesn't exist or the path escapes the council folder.
        app.MapPost("/api/projects/{projectName}/design/council/{fileName}/accept", (
            string projectName,
            string fileName,
            DesignEvidenceService svc) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            var result = svc.AcceptCouncilNote(projectName, fileName);
            if (result is null)
                return Results.NotFound(new { error = "note not found or path rejected" });
            return Results.Ok(result);
        });

        app.MapPost("/api/projects/{projectName}/design/actions/screenshot-critique", (
            string projectName,
            HttpContext ctx,
            JobScannerService scanner,
            JobMutationService mutations,
            DesignEvidenceService design) =>
            QueueDesignActionJob(projectName, ctx, scanner, mutations, design,
                ScreenshotCritiqueTitlePrefix, BuildScreenshotCritiquePrompt));

        app.MapPost("/api/projects/{projectName}/design/actions/council-review", (
            string projectName,
            HttpContext ctx,
            JobScannerService scanner,
            JobMutationService mutations,
            DesignEvidenceService design) =>
            QueueDesignActionJob(projectName, ctx, scanner, mutations, design,
                CouncilReviewTitlePrefix, BuildCouncilReviewPrompt));

        app.MapPost("/api/projects/{projectName}/design/actions/request-next-version", (
            string projectName,
            HttpContext ctx,
            JobScannerService scanner,
            JobMutationService mutations,
            DesignEvidenceService design) =>
            QueueDesignActionJob(projectName, ctx, scanner, mutations, design,
                NextVersionTitlePrefix, BuildNextVersionPrompt));
    }

    /// <summary>
    /// Queues a design-loop job. Each click is a separate CLI invocation
    /// (Hard Rules: "A spawned check is a separate CLI invocation; one
    /// active coding task per project"). The duplicate guard rejects a
    /// second click while an identical action is still pending or
    /// running on the same project.
    /// </summary>
    private static IResult QueueDesignActionJob(
        string projectName,
        HttpContext ctx,
        JobScannerService scanner,
        JobMutationService mutations,
        DesignEvidenceService design,
        string titlePrefix,
        Func<string, string> promptBuilder)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return Results.BadRequest(new { error = "project required" });
        var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

        // Refuse duplicate work in the open lanes (taxonomy: this honours
        // single-active-task-per-project without locking the runner).
        var openLanes = new[] { "1-preparation", "1a-orchestrator-prep", "1b-needs-human-review", "2-ready", "3-progress" };
        var existing = scanner.ScanAllJobs().FirstOrDefault(j =>
            string.Equals(j.WatchPath, entry.Path, StringComparison.OrdinalIgnoreCase) &&
            openLanes.Contains(j.State, StringComparer.OrdinalIgnoreCase) &&
            (j.Title?.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase) ?? false));
        if (existing is not null)
        {
            return Results.Conflict(new
            {
                error = "design-action-already-pending",
                message = $"A '{titlePrefix}' job is already in {existing.State} on this project ({existing.Id}).",
                jobId = existing.Id,
                state = existing.State,
            });
        }

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
        var slugStem = titlePrefix
            .Replace("Design: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(' ', '-')
            .ToLowerInvariant();
        var slug = $"design-{slugStem}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var ownerHeader = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
        var req = new CreateJobRequest
        {
            Id = slug,
            Title = $"{titlePrefix} {stamp}",
            Agent = "claude",
            CliType = "claude",
            WatchPath = entry.Path,
            TargetState = "2-ready",
            PromptMarkdown = promptBuilder(projectName),
            OwnerClientId = string.IsNullOrWhiteSpace(ownerHeader) ? null : ownerHeader,
        };
        var jobId = mutations.CreateJob(req);
        if (jobId is null)
            return Results.Conflict(new { error = "create-failed", message = "Job already exists or invalid input." });
        return Results.Ok(new DesignActionQueueResponse(jobId, "2-ready", req.Title));
    }

    private static string BuildScreenshotCritiquePrompt(string projectName) =>
$@"# Screenshot critique

Run the `/screenshot-critique` skill against this project's recent UI screenshots and accepted design references. Read the existing `design/` folder for context (brief, references, prior council). Write the resulting evidence file under `design/council/` using the file name `YYYY-MM-DD-screenshot-critique.md` (today's date in UTC).

The first block of the file must be YAML frontmatter so the project UX/UI panel can render the row without re-parsing the prose:

```yaml
---
date: YYYY-MM-DD
category: visual|polish|workflow|a11y|product|interaction
title: <one-line headline, e.g. ""Visual Design"">
summary: <one-sentence summary>
---
```

This is *evidence*, not a workflow trigger. Findings should not mutate task state. Follow-up work, if any, belongs as a normal queued task.

When the critique is complete, end the run with `[[TASK_DONE]]`.
";

    private static string BuildCouncilReviewPrompt(string projectName) =>
$@"# Council review

Run the `/council-review` skill against this project's design evidence. Produce a multi-role critique (Product, Visual Design, Interaction Design, Frontend Engineering, Accessibility). Each role gets its own Markdown file under `design/council/` using the file name `YYYY-MM-DD-<role>.md` (today's date in UTC).

Each file's first block must be YAML frontmatter:

```yaml
---
date: YYYY-MM-DD
category: workflow|polish|a11y|product|visual|interaction
title: <role name>
summary: <one-sentence take>
---
```

The council is *advisory evidence*. Do not mutate task state. Follow-up work belongs as a normal queued task.

When the review is complete, end the run with `[[TASK_DONE]]`.
";

    private static string BuildNextVersionPrompt(string projectName) =>
$@"# Request next design version

Read the project's `design/` folder (brief, accepted references, council critique). Produce a Markdown plan for the next iteration: which council items it addresses, which screenshots it should regenerate, which references should change kind. Write it to `design/loop.md`, replacing the previous content.

Frontmatter at the top:

```yaml
---
status: iteration-active
lastAction: request-next-version
lastCouncil: <date of most recent council file or null>
---
```

This is a planning artifact. The actual implementation is a separate queued task; do not start coding from this run.

When the plan is written, end the run with `[[TASK_DONE]]`.
";
}
