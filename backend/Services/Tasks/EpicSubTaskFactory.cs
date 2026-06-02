using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Shared creation path for an epic's sub-tasks (assignment way 3). Both the
/// deterministic endpoint (<c>POST /api/epics/{id}/sub-tasks</c>) and the
/// runner's planning/decomposition run produce a list of
/// <see cref="EpicSubTaskSpec"/> and need the same side-effect: create one
/// card per spec under the epic, with <see cref="TaskInfo.EpicId"/> set so it
/// round-trips through the scanner as a sub-task. Keeping the loop here means
/// the two callers cannot drift on defaults (CLI / model inheritance, the
/// skip-blank-title rule). The only thing they vary is the target lane.
/// </summary>
public static class EpicSubTaskFactory
{
    /// <summary>
    /// Create one card per non-blank-title spec under <paramref name="epic"/>,
    /// landing in <paramref name="targetState"/>. Each sub-task inherits the
    /// epic's CLI and model unless the spec overrides them. A blank title is
    /// skipped (not an error), so a partially-good plan still lands its valid
    /// entries. Returns the ids that were created.
    /// </summary>
    public static IReadOnlyList<string> CreateSubTasks(
        TaskMutationService mutations,
        TaskInfo epic,
        IReadOnlyList<EpicSubTaskSpec>? specs,
        string targetState)
    {
        var created = new List<string>();
        if (specs is null) return created;
        foreach (var spec in specs)
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
                TargetState = targetState,
            });
            if (id is not null) created.Add(id);
        }
        return created;
    }
}
