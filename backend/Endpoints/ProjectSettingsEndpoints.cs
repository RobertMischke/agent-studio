using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Per-project preferences under <c>/api/projects</c> — read-all
/// for the header bar plus the per-project auto-commit toggle.
/// </summary>
public static class ProjectSettingsEndpoints
{
    public static void MapProjectSettingsEndpoints(this WebApplication app)
    {
        // Per-project preferences (auto-commit on/off today). Read-all returns a
        // flat map keyed by project name so the header can render every toggle
        // in one shot without N round-trips.
        app.MapGet("/api/projects/settings", (ProjectSettingsService settings) =>
        {
            return Results.Ok(settings.GetAll());
        });

        app.MapPut("/api/projects/{projectName}/auto-commit", (string projectName, SetAutoCommitRequest req, ProjectSettingsService settings, JobScannerService scanner) =>
        {
            // Reject unknown project names so a typo in the URL fails loud rather than silently
            // adding orphan settings entries that never reach a board column.
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutoCommit(projectName, req.Enabled);
            return Results.Ok(settings.Get(projectName));
        });

        // The orchestrator's model can be tuned per project. Defaults to
        // Opus when null. This is the only knob today; per-call overrides
        // are not exposed.
        app.MapPut("/api/projects/{projectName}/orchestrator-model", (string projectName, SetOrchestratorModelRequest req, ProjectSettingsService settings, JobScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetOrchestratorModel(projectName, req.Model);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0026: per-project autonomy slider for the orchestrator-prep
        // loop. Returns the resolved level (default 2 when not set) so the
        // header can render the slider without a second round-trip.
        app.MapGet("/api/projects/{projectName}/autonomy", (string projectName, ProjectSettingsService settings, JobScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            return Results.Ok(new { level = s.AutonomyLevel ?? 2 });
        });

        app.MapPut("/api/projects/{projectName}/autonomy", (string projectName, SetAutonomyLevelRequest req, ProjectSettingsService settings, JobScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutonomyLevel(projectName, req.Level);
            return Results.Ok(new { level = settings.Get(projectName).AutonomyLevel ?? 2 });
        });
    }
}
