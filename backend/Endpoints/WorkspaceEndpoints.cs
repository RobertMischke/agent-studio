using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Workspace-wide read surfaces under <c>/api/workspace</c>: views that
/// fold across every watched project. Today the only entry is the
/// token-usage timeline that powers the workspace token view; further
/// workspace-level views (drift, audits, design council) will land
/// here over time.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspace");

        // Workspace token-usage timeline. Returns one cell per
        // (project, time bucket) with input/output/cache/total token
        // counts plus a theoretical dollar estimate when the model is
        // priced. windowHours defaults to 24 and accepts {1, 6, 24, 168};
        // bucketMinutes defaults to 60 and accepts {5, 15, 60}. Out-of-
        // range values snap to the defaults rather than failing - the
        // status-bar entry into this view should always render.
        group.MapGet("/tokens/timeline",
            (int? windowHours, int? bucketMinutes, JobScannerService scanner, WorkspaceTokensTimelineService timeline) =>
            {
                var projects = scanner.GetWatchPaths()
                    .Select(e => (e.Name, e.Path))
                    .ToList();
                var result = timeline.Build(
                    projects,
                    windowHours ?? WorkspaceTokensTimelineService.DefaultWindowHours,
                    bucketMinutes ?? WorkspaceTokensTimelineService.DefaultBucketMinutes);
                return Results.Ok(result);
            });

        // Workspace-wide visual evidence reel. Folds the per-job
        // results/ scan over every watched job touched in the
        // requested window, ordered newest-first. windowHours
        // defaults to 72 (three days) and is clamped to >= 1; an
        // optional projectFilter (project display name) narrows the
        // result. Drives the "Visual evidence" reel overlay.
        group.MapGet("/screenshots",
            (int? windowHours, string? projectFilter, ScreenshotIndexService screenshots) =>
            {
                var hours = Math.Max(1, windowHours ?? 72);
                var entries = screenshots.ListWorkspaceScreenshots(hours, projectFilter);
                return Results.Ok(new WorkspaceScreenshotsResponse
                {
                    WindowHours = hours,
                    ProjectFilter = projectFilter,
                    Screenshots = entries.ToList()
                });
            });

        // Workspace executive summary. Folds per-project activity
        // (job moves, supervisor advisories, repository commits) plus
        // workspace-level crash records and any open
        // human-decision-needed-* tasks into one snapshot covering the
        // requested window. windowHours defaults to 24 and accepts
        // {1, 6, 24, 168}; out-of-range values snap to the default
        // so the page always renders. Schema:
        // docs/schemas/executive-summary.schema.json.
        group.MapGet("/summary",
            (int? windowHours, WorkspaceSummaryService summary) =>
            {
                var result = summary.Build(windowHours ?? WorkspaceSummaryService.DefaultWindowHours);
                return Results.Ok(result);
            });
    }
}
