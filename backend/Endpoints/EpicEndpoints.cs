using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Epics read + decomposition routes. An epic is a <see cref="TaskKinds.Epic"/>
/// card; its sub-tasks are the tasks whose <see cref="TaskInfo.EpicId"/> points
/// at it. There is no separate epic store - the rollup is derived live from the
/// scanner so progress always matches the board. Epic <em>creation</em> reuses
/// <c>POST /api/tasks</c> (kind=epic), and assignment ways 1 &amp; 2 live on the
/// tasks group; this file adds the rollup (<c>GET /api/epics</c>) and the
/// deterministic half of way 3 (<c>POST /api/epics/{id}/sub-tasks</c>).
/// </summary>
public static class EpicEndpoints
{
    public static void MapEpicEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/epics");

        // List every epic with a live sub-task rollup. includeFixtures mirrors
        // the tasks list so test fixtures don't leak into the normal view.
        group.MapGet("/", (bool? includeFixtures, TaskScannerService scanner) =>
        {
            var all = scanner.ScanAllJobs();
            if (includeFixtures != true) all = all.Where(j => !j.Fixture).ToList();
            var rollups = all
                .Where(t => TaskKinds.IsEpic(t.Kind))
                .OrderBy(e => e.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Order)
                .Select(e => BuildRollup(e, all))
                .ToList();
            return Results.Ok(rollups);
        });

        // Single epic rollup. 404 when the id is unknown or is not an epic.
        group.MapGet("/{epicId}", (string epicId, string? watchPath, TaskScannerService scanner) =>
        {
            var epic = scanner.FindJob(epicId, watchPath);
            if (epic is null || !TaskKinds.IsEpic(epic.Kind)) return Results.NotFound();
            return Results.Ok(BuildRollup(epic, scanner.ScanAllJobs()));
        });

        // Way 3 (deterministic half): create one or more sub-tasks under an
        // epic. The epic's decomposition/planning run produces the specs; the
        // LLM that authors titles/prompts is the orchestrator side and is wired
        // separately. Sub-tasks land in the epic's project, in 0-backlog for
        // triage, with epicId set (so they round-trip through the same scanner
        // path as assignment way 1). Per-item: a blank title is skipped, not
        // an error, so a partially-good plan still lands its valid sub-tasks.
        group.MapPost("/{epicId}/sub-tasks", (string epicId, string? watchPath, CreateEpicSubTasksRequest req, TaskScannerService scanner, TaskMutationService mutations) =>
        {
            var epic = scanner.FindJob(epicId, watchPath);
            if (epic is null || !TaskKinds.IsEpic(epic.Kind)) return Results.NotFound();
            if (req?.SubTasks is null || req.SubTasks.Count == 0)
                return Results.BadRequest(new { error = "subTasks is required and must contain at least one entry" });

            var created = new List<string>();
            foreach (var spec in req.SubTasks)
            {
                if (string.IsNullOrWhiteSpace(spec.Title)) continue;
                var id = mutations.CreateJob(new CreateJobRequest
                {
                    Title = spec.Title,
                    WatchPath = epic.WatchPath,
                    EpicId = epic.Id,
                    PromptMarkdown = spec.PromptMarkdown,
                    CliType = spec.CliType ?? epic.CliType,
                    Model = spec.Model ?? epic.Model,
                    TargetState = TaskStates.Backlog,
                });
                if (id is not null) created.Add(id);
            }
            return Results.Ok(new { epicId = epic.Id, created });
        });
    }

    /// <summary>
    /// Derives an <see cref="EpicRollup"/> from the live scan. Progress is
    /// bucketed so the math never drops a lane: completed = 6-completed +
    /// 7-archive, open = 0-backlog + 2-ready, and in-progress is whatever is
    /// left (preparation, orchestrator-prep, ready-exclusive middle lanes,
    /// review, failed-pickup). ByState keeps the raw per-lane counts for a
    /// detailed view.
    /// </summary>
    internal static EpicRollup BuildRollup(TaskInfo epic, IReadOnlyList<TaskInfo> all)
    {
        var subs = all.Where(t => string.Equals(t.EpicId, epic.Id, StringComparison.Ordinal)).ToList();
        var byState = subs.GroupBy(s => s.State).ToDictionary(g => g.Key, g => g.Count());
        int Count(params string[] lanes) => subs.Count(s => lanes.Contains(s.State));
        var completed = Count(TaskStates.Completed, TaskStates.Archive);
        var open = Count(TaskStates.Backlog, TaskStates.Ready);
        var inProgress = subs.Count - completed - open;
        return new EpicRollup(
            epic.Id, epic.Title, epic.ProjectName, epic.WatchPath, epic.State,
            subs.Count, completed, inProgress, open, byState,
            subs.OrderBy(s => s.Order)
                .Select(s => new EpicSubTaskRef(s.Id, s.Title, s.State, s.Order))
                .ToList());
    }
}
