namespace AgentStudio.TestRuns;

public static class TestRunEndpoints
{
    public static void MapTestRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{project}/test-runs", (string project, TestRunService service) =>
        {
            var response = service.BuildProjectView(project);
            return response is null
                ? Results.NotFound(new { error = $"Unknown project '{project}'" })
                : Results.Ok(response);
        });

        app.MapPost("/api/projects/{project}/test-runs", (string project, CreateTestRunRequest request, TestRunService service) =>
        {
            try
            {
                var run = service.Create(project, request);
                return run is null
                    ? Results.NotFound(new { error = $"Unknown project '{project}'" })
                    : Results.Created($"/api/projects/{Uri.EscapeDataString(project)}/test-runs/{run.Id}", run);
            }
            catch (TestRunValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/projects/{project}/test-runs/{runId}", (string project, string runId, UpdateTestRunRequest request, TestRunService service) =>
        {
            try
            {
                if (service.ResolveProject(project) is null)
                    return Results.NotFound(new { error = $"Unknown project '{project}'" });
                var run = service.Update(project, runId, request);
                return run is null ? Results.NotFound(new { error = $"Unknown test run '{runId}'" }) : Results.Ok(run);
            }
            catch (TestRunValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
}
