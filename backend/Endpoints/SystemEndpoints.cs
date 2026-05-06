using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Cross-cutting routes that don't fit any of the resource-scoped
/// groups: workspace enumeration, environment flags consumed by the
/// frontend bootstrap, the agent-rules overlay descriptor, the global
/// git-summary cache for board pills, and the health probe.
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/watch-paths", (JobScannerService scanner) =>
        {
            var entries = scanner.GetWatchPaths();
            return Results.Ok(entries);
        });

        // Returns flags that vary by per-checkout local config (appsettings.Local.json).
        // The frontend reads this before bootstrap to decide whether to show the DEV
        // banner and swap the PWA icon / favicon to the dev variants.
        app.MapGet("/api/environment", (IConfiguration config) =>
        {
            var isDev = config.GetValue<bool>("Environment:IsDev");
            var devTools = new
            {
                updateStableEnabled = config.GetValue<bool>("DevTools:UpdateStableEnabled"),
                deleteE2EJobsEnabled = config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled")
            };
            return Results.Ok(new { isDev, devTools });
        });

        // Lists the centrally-managed agent-rule files that are appended as a
        // system-prompt overlay to every Claude job. Used by the Job Detail
        // header to show "Active rules" so the user can verify what's in scope.
        app.MapGet("/api/agent-rules", (IConfiguration config) =>
        {
            var configured = config["AgentRules:CorePath"];
            if (string.IsNullOrWhiteSpace(configured))
                return Results.Ok(Array.Empty<object>());

            var candidates = new List<string>();
            if (Path.IsPathRooted(configured))
            {
                candidates.Add(configured);
            }
            else
            {
                candidates.Add(Path.GetFullPath(configured));
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    candidates.Add(Path.Combine(dir.FullName, configured));
                    dir = dir.Parent;
                }
            }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                var fi = new FileInfo(candidate);
                return Results.Ok(new[]
                {
                    new
                    {
                        name = Path.GetFileName(candidate),
                        path = candidate,
                        sizeBytes = fi.Length,
                        modifiedAt = fi.LastWriteTimeUtc
                    }
                });
            }
            return Results.Ok(Array.Empty<object>());
        });

        // Per-project git summary, used by board tile pills. Cached server-side
        // for ~3 s so the board can call freely without forking N git processes.
        app.MapGet("/api/git/summary", (GitService git) => Results.Ok(git.GetSummaries()));

        // Repository hygiene snapshot for the project header badge: dirty
        // working tree, ahead-of-upstream, last commit, etc. Cached for ~3 s
        // and deliberately separate from /api/git/summary because the
        // hygiene shape carries upstream + last-commit info the board pills
        // do not need.
        app.MapGet("/api/git/hygiene", (string project, GitService git) =>
            Results.Ok(git.GetProjectHygiene(project)));

        app.MapGet("/healthz", () => Results.Ok("ok"));
    }
}
