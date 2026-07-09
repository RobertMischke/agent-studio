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

        static IResult PostTurn(string contextKey, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns)
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out _))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Prompt is required." });

            return Results.Accepted($"/api/orchestrator/sessions/{contextKey}", turns.Enqueue(contextKey, request));
        }

        static IResult Park(string contextKey, OrchestratorSessionRegistry registry, OrchestratorTurnService turns)
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out _))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            return Results.Ok(turns.Park(contextKey));
        }

        group.MapPost("/global/turns", (OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn("global", request, registry, turns));
        group.MapPost("/project:{projectId}/turns", (string projectId, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn($"project:{projectId}", request, registry, turns));
        group.MapPost("/task:{projectId}/{taskKey}/turns", (string projectId, string taskKey, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn($"task:{projectId}/{taskKey}", request, registry, turns));

        group.MapPost("/global/park", (OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park("global", registry, turns));
        group.MapPost("/project:{projectId}/park", (string projectId, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park($"project:{projectId}", registry, turns));
        group.MapPost("/task:{projectId}/{taskKey}/park", (string projectId, string taskKey, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park($"task:{projectId}/{taskKey}", registry, turns));
    }
}
