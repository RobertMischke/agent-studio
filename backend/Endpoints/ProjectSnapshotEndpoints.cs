using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Cycle 5: <c>GET /api/projects/{projectName}/snapshot</c>. One round-trip
/// that returns every per-project field the project-detail panel polled
/// individually pre-Cycle-5 (settings, runner status for the project,
/// orchestrator log tail, orchestrator session, post-run review-decisions
/// pending, live runner pending decisions). The pre-Cycle-5 panel
/// fan-out was 6+ HTTP requests every 5 s; with the snapshot it is one
/// request, and the per-project call shape stays cache-friendly inside
/// the backend (every read goes through the JobIndexCache from Cycle 1).
///
/// <para>The standalone endpoints stay live so existing consumers keep
/// working - the snapshot is purely additive. project-detail is the only
/// caller that fans out across all of them on a single tick, which is
/// why the snapshot is project-detail-shaped and not a generic
/// "everything I might ever need" mega-endpoint.</para>
/// </summary>
public static class ProjectSnapshotEndpoints
{
    // Cycle 5 review-decisions cache. The 4-auto-review scan walks every
    // job folder in the lane and reads cli-output.log to find unresolved
    // [[TASK_NEEDS_INPUT]] sentinels - that's ~225 ms p95 against the
    // real workspace and dominates the snapshot's response time. The
    // frontend polls the snapshot every 5 s, so a TTL just under that
    // means the second poll always hits cache, the first polls
    // through, and a fresh sentinel still appears within 5 s.
    // Per-(workspace, project, lane-mtime) key so the cache invalidates
    // automatically when the lane folder content changes.
    private sealed record ReviewCacheEntry(DateTime CapturedAt, long LaneMtimeTicks, List<object> Items);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReviewCacheEntry> ReviewCache = new();
    private static readonly TimeSpan ReviewCacheTtl = TimeSpan.FromSeconds(3);
    public static long ReviewCacheHits;
    public static long ReviewCacheMisses;

    public static void MapProjectSnapshotEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/snapshot", (
            string projectName,
            JobScannerService scanner,
            TaskRunnerService runner,
            ProjectSettingsService settingsSvc,
            OrchestratorLog log,
            OrchestratorSessionStore sessionStore) =>
        {
            // Resolve the watch path once. Every per-project field below
            // either filters from cached state or reads a single
            // workspace file; none of them re-scan disk for the job
            // catalogue (JobIndexCache from Cycle 1 covers that).
            var entries = scanner.GetWatchPaths();
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            // 1) Settings (auto-commit, runner mode, orchestrator model).
            var settings = settingsSvc.Get(projectName);

            // 2) Runner status snapshot. We pluck just this project's slot
            //    out of the full RunnerStatus to keep payload tight - the
            //    board endpoint already serves the full shape.
            var fullStatus = runner.GetStatus();
            ProjectRunnerStatus? projectRunner = null;
            if (fullStatus.Projects != null && fullStatus.Projects.TryGetValue(projectName, out var ps))
            {
                projectRunner = ps;
            }

            // 3) Orchestrator log tail (last 5 entries, newest first - matches
            //    project-detail.refreshAll's existing slice).
            var allLog = log.Read(entry.Path);
            var logTail = allLog.Skip(Math.Max(0, allLog.Count - 5)).Reverse().ToList();

            // 4) Orchestrator session.
            var session = sessionStore.Read(entry.Path);

            // 5) Post-run pending review decisions. Cached at endpoint
            //    level on (workspace, project, lane-mtime) for 3 s - the
            //    frontend polls every 5 s, so the second poll hits cache,
            //    the first pays the disk walk, and a fresh sentinel still
            //    surfaces within one poll cycle. Without the cache this
            //    one scan dominated the snapshot at ~225 ms p95.
            var reviewDecisions = ReadReviewDecisionsCached(entry.Path);

            // 6) Live runner pending decisions (during an active run, the
            //    sentinel emitted by the in-flight job).
            var livePending = runner.GetPendingDecisions(projectName) ?? Array.Empty<PendingDecisionEntry>();
            var liveItems = livePending.Select(e => new
            {
                jobId = e.JobId,
                title = e.Title,
                kind = e.Decision.Kind switch
                {
                    PendingDecisionKind.NeedsInput => "needs-input",
                    PendingDecisionKind.Blocked => "blocked",
                    _ => "unknown"
                },
                reason = e.Decision.Reason,
                detectedAt = e.Decision.DetectedAt
            }).ToList();

            return Results.Ok(new
            {
                project = projectName,
                capturedAt = DateTime.UtcNow,
                settings = new
                {
                    autoCommit = settings.AutoCommit,
                    runnerMode = settings.RunnerMode,
                    orchestratorModel = settings.OrchestratorModel
                },
                runnerStatus = projectRunner == null ? null : new
                {
                    projectRunner.ProjectName,
                    projectRunner.Mode,
                    projectRunner.ActiveJobId,
                    projectRunner.ActiveExecution,
                    projectRunner.QueuedJobIds
                },
                orchestratorLogTail = logTail,
                orchestratorSession = session,
                reviewDecisionsPending = reviewDecisions,
                runnerPendingDecisions = liveItems,
                _diag = new { reviewHits = ReviewCacheHits, reviewMisses = ReviewCacheMisses }
            });
        });
    }

    private static List<object> ReadReviewDecisionsCached(string watchPath)
    {
        var reviewDir = Path.Combine(watchPath, JobStates.AutoReview);
        if (!Directory.Exists(reviewDir)) return new List<object>();

        long laneMtime;
        try { laneMtime = Directory.GetLastWriteTimeUtc(reviewDir).Ticks; }
        catch { laneMtime = 0; }

        if (ReviewCache.TryGetValue(watchPath, out var cached))
        {
            if (cached.LaneMtimeTicks == laneMtime
                && DateTime.UtcNow - cached.CapturedAt < ReviewCacheTtl)
            {
                Interlocked.Increment(ref ReviewCacheHits);
                return cached.Items;
            }
        }
        Interlocked.Increment(ref ReviewCacheMisses);

        var items = new List<object>();
        foreach (var jobDir in Directory.GetDirectories(reviewDir))
        {
            var logPath = JobPaths.CliOutputLog(jobDir);
            if (!File.Exists(logPath)) continue;
            string body;
            try { body = File.ReadAllText(logPath); } catch { continue; }
            var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(body);
            if (needs == null) continue;

            string id = Path.GetFileName(jobDir);
            string title = id;
            var jobJsonPath = Path.Combine(jobDir, "job.json");
            if (File.Exists(jobJsonPath))
            {
                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jobJsonPath));
                    if (doc.RootElement.TryGetProperty("id", out var idEl)) id = idEl.GetString() ?? id;
                    if (doc.RootElement.TryGetProperty("title", out var tEl)) title = tEl.GetString() ?? id;
                }
                catch { /* best-effort metadata */ }
            }
            items.Add(new { jobId = id, title, reason = needs.Reason });
        }

        ReviewCache[watchPath] = new ReviewCacheEntry(DateTime.UtcNow, laneMtime, items);
        return items;
    }
}
