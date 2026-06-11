

namespace AgentStudio.Runtime;

/// <summary>
/// Body for <c>POST /api/projects/{projectName}/skill-readiness/fix-task</c>.
/// All fields are optional; the body itself may be absent. The owner
/// client id falls back to the request's <c>X-Client-Id</c> when omitted.
/// </summary>
public record SkillReadinessFixTaskBody
{
    public string? OwnerClientId { get; init; }
}

/// <summary>
/// Project-level skill readiness surface (docs/product/skills-architecture.md
/// "First Product Step"). Three reads + one mutation:
///
/// - <c>GET /api/projects/{projectName}/skill-readiness</c> -- pass / warn /
///   fail verdict on the README lookup section.
/// - <c>GET /api/projects/{projectName}/skill-readiness/fix-task-preview</c> --
///   the title and prompt the fix path would queue, without creating it.
/// - <c>GET /api/projects/{projectName}/skills</c> -- catalog of the standard
///   library plus this project's project-specific skills, with
///   selected vs. suggested annotations.
/// - <c>POST /api/projects/{projectName}/skill-readiness/fix-task</c> --
///   queues the fix task in the project's <c>2-ready</c> lane and
///   returns the new job id.
/// </summary>
public static class SkillReadinessEndpoints
{
    public static void MapSkillReadinessEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/skill-readiness",
            (string projectName, SkillReadinessService svc) =>
            {
                var report = svc.CheckProject(projectName);
                return report == null
                    ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                    : Results.Ok(report);
            });

        app.MapGet("/api/projects/{projectName}/skill-readiness/fix-task-preview",
            (string projectName, SkillReadinessService svc) =>
            {
                var preview = svc.PreviewFixTask(projectName);
                return preview == null
                    ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                    : Results.Ok(preview);
            });

        app.MapGet("/api/projects/{projectName}/skills",
            (string projectName, SkillReadinessService svc) =>
            {
                var catalog = svc.GetCatalog(projectName);
                return Results.Ok(catalog);
            });

        app.MapPost("/api/projects/{projectName}/skill-readiness/fix-task",
            (string projectName, SkillReadinessFixTaskBody? body, HttpRequest http, SkillReadinessService svc) =>
            {
                // Owner identity falls back to the request's X-Client-Id, then
                // to the TaskMutationService's default. Same chain CreateJobRequest
                // documents under OwnerClientId.
                var owner = body?.OwnerClientId;
                if (string.IsNullOrWhiteSpace(owner)
                    && http.Headers.TryGetValue("X-Client-Id", out var hdr))
                {
                    owner = hdr.ToString();
                }

                var result = svc.CreateFixTask(projectName, owner);
                return result == null
                    ? Results.NotFound(new { error = $"Unknown project '{projectName}' or could not create fix task." })
                    : Results.Ok(result);
            });
    }
}
