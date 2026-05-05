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
    }
}
