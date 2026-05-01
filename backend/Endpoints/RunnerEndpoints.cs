using OrchestratorApi.Models;
using OrchestratorApi.Services;

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
    }
}
