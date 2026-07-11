

using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Merge / consolidation API for the
/// <c>api-consolidationmerge-api--completed-lane-audit--agent-claimed-done-but-isnt-re-evaluation</c>
/// task. Five routes mounted under <c>/api/tasks</c>:
///
/// <list type="bullet">
/// <item><c>GET /{id}/merge/candidates</c> - heuristic wrapper detection.</item>
/// <item><c>POST /{primaryId}/merge/preview</c> - dry-run timeline-events + conflicts.</item>
/// <item><c>POST /{primaryId}/merge</c> - real consolidate (default), absorb, or link-only.</item>
/// <item><c>POST /{primaryId}/merge/undo</c> - 24h restore from <c>.audit/merges.jsonl</c> token.</item>
/// <item><c>POST /{id}/re-evaluate</c> - completed-lane "really done?" check.</item>
/// </list>
///
/// Every mutation row through <see cref="MergeService"/> stamps the
/// caller's <c>X-Client-Id</c> (or "unknown") onto the audit-log row
/// so the operator trail survives a backend restart.
/// </summary>
public static class TaskMergeEndpoints
{
    public static void MapTaskMergeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{primaryId}/merge/candidates",
            (string primaryId, string? project, string? watchPath, MergeService merges, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = merges.FindCandidates(primaryId, watchPath);
            return Results.Ok(result);
        });

        group.MapPost("/{primaryId}/merge/preview",
            (string primaryId, string? project, string? watchPath, MergeRequest req, MergeService merges, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (req == null) return Results.BadRequest(new { error = "request body required" });
            var outcome = merges.Preview(primaryId, watchPath, req);
            return MapPreview(outcome);
        });

        group.MapPost("/{primaryId}/merge",
            (string primaryId, string? project, string? watchPath, MergeRequest req, HttpContext ctx, MergeService merges, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (req == null) return Results.BadRequest(new { error = "request body required" });
            var who = ctx.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "unknown";
            var outcome = merges.Merge(primaryId, watchPath, req, who);
            return MapMerge(outcome);
        });

        group.MapPost("/{primaryId}/merge/undo",
            (string primaryId, MergeUndoRequest req, HttpContext ctx, MergeService merges) =>
        {
            if (req == null) return Results.BadRequest(new { error = "request body required" });
            var who = ctx.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "unknown";
            var outcome = merges.Undo(primaryId, req, who);
            return MapUndo(outcome);
        });

        group.MapPost("/{jobId}/re-evaluate",
            (string jobId, string? project, string? watchPath, HttpContext ctx, CompletedLaneAuditService audit, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var who = ctx.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "system";
            var outcome = audit.ReEvaluate(jobId, watchPath, who);
            return outcome.Status switch
            {
                ReEvaluateStatus.Success => Results.Ok(outcome.Response),
                ReEvaluateStatus.TaskNotFound => Results.NotFound(),
                ReEvaluateStatus.WrongLane => Results.BadRequest(new { error = outcome.Message }),
                _ => Results.Problem(outcome.Message ?? "re-evaluate failed"),
            };
        });
    }

    private static IResult MapMerge(MergeOutcome outcome) => outcome.Status switch
    {
        MergeStatus.Success => Results.Ok(outcome.Response),
        MergeStatus.PrimaryNotFound => Results.NotFound(new { error = outcome.Message ?? "primary not found" }),
        MergeStatus.SecondaryNotFound => Results.NotFound(new { error = outcome.Message ?? "secondary not found" }),
        MergeStatus.SameJob => Results.BadRequest(new { error = outcome.Message ?? "primary and secondary must differ" }),
        MergeStatus.DifferentProject => Results.BadRequest(new { error = outcome.Message ?? "different project" }),
        MergeStatus.InvalidMode => Results.BadRequest(new { error = outcome.Message ?? "invalid mode" }),
        MergeStatus.AlreadyMerged => Results.Conflict(new { error = outcome.Message ?? "already merged" }),
        MergeStatus.ArchiveCollision => Results.Conflict(new { error = outcome.Message ?? "archive collision" }),
        _ => Results.Problem(outcome.Message ?? "merge failed"),
    };

    private static IResult MapPreview(MergeOutcome outcome)
    {
        if (outcome.Status != MergeStatus.Success) return MapMerge(outcome);
        // Preview piggy-backs the MergePreviewResponse JSON in
        // outcome.Message so we don't have to thread two response types.
        var json = outcome.Message ?? "{}";
        return Results.Content(json, "application/json");
    }

    private static IResult MapUndo(MergeUndoOutcome outcome) => outcome.Status switch
    {
        MergeUndoStatus.Success => Results.Ok(outcome.Response),
        MergeUndoStatus.TokenNotFound => Results.NotFound(new { error = outcome.Message ?? "token not found" }),
        MergeUndoStatus.Expired => Results.BadRequest(new { error = outcome.Message ?? "expired" }),
        MergeUndoStatus.AlreadyRestored => Results.Conflict(new { error = outcome.Message ?? "already restored" }),
        MergeUndoStatus.ArchiveMissing => Results.Conflict(new { error = outcome.Message ?? "archive missing" }),
        MergeUndoStatus.PrimaryNotFound => Results.NotFound(new { error = outcome.Message ?? "primary not found" }),
        _ => Results.Problem(outcome.Message ?? "undo failed"),
    };
}
