using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Project-runner control surface under <c>/api/runner</c>: the
/// status snapshot the board polls plus the manual mode / start /
/// stop toggles. Per-job execution lives in
/// <see cref="OrchestratorApi.Endpoints.Jobs.JobRunnerEndpoints"/> —
/// these routes operate at project granularity.
/// </summary>
public static class RunnerEndpoints
{
    public static void MapRunnerEndpoints(this WebApplication app)
    {
        var runnerGroup = app.MapGroup("/api/runner");

        runnerGroup.MapGet("/status", (TaskRunnerService runner) =>
        {
            return Results.Ok(runner.GetStatus());
        });

        runnerGroup.MapPut("/{projectName}/mode", (string projectName, SetRunnerModeRequest req, TaskRunnerService runner) =>
        {
            var success = runner.SetMode(projectName, req.Mode);
            return success ? Results.Ok() : Results.BadRequest("Invalid project or mode");
        });

        runnerGroup.MapPost("/{projectName}/start", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StartRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        });

        runnerGroup.MapPost("/{projectName}/stop", (string projectName, TaskRunnerService runner) =>
        {
            var success = runner.StopRunner(projectName);
            return success ? Results.Ok() : Results.NotFound();
        });

        // Orchestrator log: chronological feed of decisions / actions /
        // observations / interventions for the named project. Read-only;
        // entries are appended by the runner today and (Phase D+) by a
        // dedicated orchestrator process. The frontend renders this as
        // the "Orchestrator" feed in the project detail view.
        runnerGroup.MapGet("/{projectName}/orchestrator-log",
            (string projectName, JobScannerService scanner, OrchestratorLog log) =>
            {
                var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
                if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var entries = log.Read(entry.Path);
                return Results.Ok(new { project = projectName, entries });
            });
    }
}
