namespace AgentStudio.Publishing;

/// <summary>
/// PUB-1 - read-only publish-status route. <c>GET
/// /api/projects/{project}/publish-status</c> returns the project's derived
/// publish targets (package + website) and their pending deltas, straight from
/// <see cref="PublishTargetService"/>. Additive to the project snapshot (which
/// folds the same status in under <c>publishTargets</c> for the Hub overview
/// poll); the standalone route lets the Git View and tests read it directly.
/// </summary>
public static class PublishEndpoints
{
    public static void MapPublishEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{project}/publish-status", (string project, PublishTargetService publish) =>
            Results.Ok(publish.GetProjectPublishStatus(project)));
    }
}
