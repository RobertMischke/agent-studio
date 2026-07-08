namespace AgentStudio.Orchestrator;

public static class OrchestratorSessionEndpoints
{
    public static void MapOrchestratorSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orchestrator/sessions");

        group.MapGet("", (OrchestratorSessionRegistry registry) =>
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            return Results.Ok(new
            {
                root = registry.SessionsRoot,
                sessions = registry.List()
            });
        });

        group.MapGet("/{**contextKey}", (string contextKey, OrchestratorSessionRegistry registry) =>
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out _))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            return Results.Ok(registry.GetOrCreate(contextKey));
        });
    }
}
