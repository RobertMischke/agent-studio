

namespace AgentStudio.Review;

/// <summary>
/// Read-side surface for the <see cref="ReviewDecisionOrchestrator"/>:
/// per-project list of jobs in <c>4-auto-review</c> whose latest CLI output
/// carries an unresolved <c>[[TASK_NEEDS_INPUT]]</c> sentinel. Drives the
/// project-view banner that signals "the orchestrator owes a decision
/// here" so the user can either let auto-decide work or step in.
/// </summary>
public static class ReviewDecisionsEndpoints
{
    public static void MapReviewDecisionsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/review-decisions-pending",
            (string projectName, ITaskAccess taskAccess, TaskScannerService scanner) =>
        {
            // ADR-0024: enumerate the 4-auto-review lane through the
            // typed TaskAccess layer instead of building a raw lane path.
            // The scanner is still injected for GetWatchPaths so an
            // "unknown project" still produces 404 (config-level error)
            // rather than a 200 + empty list (no-pending).
            var entries = scanner.GetWatchPaths();
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var pending = new List<PendingDecisionDto>();
            foreach (var info in taskAccess.ListByLane(projectName, TaskStates.AutoReview))
            {
                var logPath = TaskPaths.CliOutputLog(info.FolderPath);
                if (!File.Exists(logPath)) continue;

                string log;
                try { log = File.ReadAllText(logPath); }
                catch { continue; }

                var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);
                if (needs == null) continue;

                pending.Add(new PendingDecisionDto(info.Id, info.Title, needs.Reason));
            }

            return Results.Ok(new PendingDecisionsResponse(projectName, pending));
        });

        // Live-status snapshot for compact auto-review indicators.
        // Polled by the FE at the orchestrator's tick cadence (default 30s)
        // so the user can see the orchestrator is alive and forming
        // opinions instead of silently waving jobs through. Returns the
        // last completed tick's accept/reissue/escalate counts plus the
        // job currently under review (if any).
        app.MapGet("/api/auto-review/status", (
            AutoReviewStatusSnapshot snapshot,
            AttemptAuthorityService authority,
            TaskScannerService scanner) =>
        {
            var tasks = scanner.ScanAllJobs()
                .Where(task => string.Equals(
                    task.State,
                    TaskStates.AutoReview,
                    StringComparison.OrdinalIgnoreCase))
                .SelectMany(task => new string?[] { task.TaskKey, task.Key, task.Id }
                    .Where(identity => !string.IsNullOrWhiteSpace(identity))
                    .Select(identity => (Identity: identity!, Task: task)))
                .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Task,
                    StringComparer.OrdinalIgnoreCase);
            var durableReviews = authority.GetActiveRunnerAttempts()
                .Where(attempt => string.Equals(
                    attempt.Kind,
                    AgentStudio.TaskServer.Contracts.RunnerAttemptKinds.Review,
                    StringComparison.Ordinal))
                .Where(attempt => tasks.ContainsKey(attempt.TaskKey))
                .Select(attempt =>
                {
                    var task = tasks[attempt.TaskKey];
                    return new AutoReviewActivityView(
                        task.ProjectName,
                        task.Id,
                        AutoReviewActivitySteps.Processing,
                        attempt.ObservedAt);
                })
                .ToList();
            return Results.Ok(snapshot.Read(durableReviews));
        });
    }
}

public sealed record PendingDecisionDto(string JobId, string Title, string? Reason);
public sealed record PendingDecisionsResponse(string Project, IReadOnlyList<PendingDecisionDto> Items);
