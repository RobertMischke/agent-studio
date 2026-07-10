namespace AgentStudio.Docs.Grading;

/// <summary>
/// HTTP surface for the wiki-grading maintenance run (AGT-2051): start / poll /
/// abort a global grading pass over a project's wiki, plus read/write the
/// workspace maintenance-model default that pre-fills the trigger. The grading
/// routes sit under the project's wiki namespace; the maintenance-model default
/// lives under <c>/api/cli</c> so it reads as part of the consolidated
/// CLI-management area.
/// </summary>
public static class WikiGradingEndpoints
{
    public static void MapWikiGradingEndpoints(this WebApplication app)
    {
        // Start a grading run. Model / level / cli default from the workspace
        // maintenance config when the body omits them, so the common case is a
        // bare POST. 409 when a run is already in flight (with its live status).
        app.MapPost("/api/projects/{projectName}/wiki/grading/run", (
            string projectName, WikiGradingRunBody? body,
            WikiGradingService grading, WikiMaintenanceModelService maintenance) =>
        {
            var def = maintenance.Get();
            var req = new WikiGradingRunRequest(
                CliType: string.IsNullOrWhiteSpace(body?.CliType) ? def.CliType : body!.CliType!.Trim(),
                Model: string.IsNullOrWhiteSpace(body?.Model) ? def.Model : body!.Model!.Trim(),
                ThinkingLevel: string.IsNullOrWhiteSpace(body?.ThinkingLevel) ? def.ThinkingLevel : body!.ThinkingLevel!.Trim(),
                Force: body?.Force ?? false,
                Limit: body?.Limit ?? 0);

            var result = grading.Start(projectName, req);
            if (result.Started) return Results.Ok(result.Status);
            return result.Status != null
                ? Results.Conflict(new { error = result.Error, status = result.Status })
                : Results.BadRequest(new { error = result.Error });
        });

        // Poll the latest run status (running or finished). `status` is null until
        // the first run is started for this project.
        app.MapGet("/api/projects/{projectName}/wiki/grading/status", (
            string projectName, WikiGradingService grading) =>
            Results.Ok(new { status = grading.GetStatus(projectName) }));

        // Request cancellation of an in-flight run. Idempotent: returns
        // `aborted=false` when there is nothing running to abort.
        app.MapPost("/api/projects/{projectName}/wiki/grading/abort", (
            string projectName, WikiGradingService grading) =>
            Results.Ok(new { aborted = grading.Abort(projectName), status = grading.GetStatus(projectName) }));

        // ---- Maintenance-model default (consolidated CLI-management area) ----

        app.MapGet("/api/cli/maintenance-model", (WikiMaintenanceModelService maintenance) =>
            Results.Ok(maintenance.Get()));

        app.MapPut("/api/cli/maintenance-model", (
            SetWikiMaintenanceModelBody body, WikiMaintenanceModelService maintenance) =>
            Results.Ok(maintenance.Set(body.CliType, body.Model, body.ThinkingLevel)));
    }
}

/// <summary>Body for <c>POST /api/projects/{p}/wiki/grading/run</c>; every field
/// is optional and falls back to the maintenance default.</summary>
public sealed record WikiGradingRunBody
{
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public bool Force { get; init; }
    public int Limit { get; init; }
}

/// <summary>Body for <c>PUT /api/cli/maintenance-model</c>.</summary>
public sealed record SetWikiMaintenanceModelBody
{
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
}
