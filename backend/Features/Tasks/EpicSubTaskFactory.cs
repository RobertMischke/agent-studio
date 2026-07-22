
namespace AgentStudio.Tasks;

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
        string targetState,
        string initiator = TaskCreationInitiators.Operator,
        string? contextKey = null)
    {
        var created = new List<string>();
        if (specs is null) return created;
        if (!EpicGoalPlanValidator.Validate(specs).IsValid) return created;

        var safeTargetState = ClampTargetState(targetState);
        var pending = specs.Where(spec => !string.IsNullOrWhiteSpace(spec.Title)).ToList();
        var processedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdByPlanId = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);

        // Materialize nodes in topological order even when the authored JSON is
        // not already sorted. A dependent is skipped if one of its prerequisite
        // cards could not be created, so it can never run without the dependency
        // that was meant to gate it.
        while (pending.Count > 0)
        {
            var index = pending.FindIndex(spec =>
                EpicGoalPlanValidator.NormalizeDependencies(spec.DependsOn)
                    .All(processedPlanIds.Contains));
            if (index < 0) break;

            var spec = pending[index];
            pending.RemoveAt(index);
            var dependencies = EpicGoalPlanValidator.NormalizeDependencies(spec.DependsOn);
            var planId = spec.PlanId?.Trim();
            var prerequisitesCreated = dependencies.All(createdByPlanId.ContainsKey);

            if (prerequisitesCreated)
            {
                var provenance = new TaskCreationProvenance
                {
                    Initiator = TaskCreationInitiators.Normalize(initiator),
                    GoalId = epic.Id,
                    GoalKey = epic.Key,
                    ContextKey = contextKey,
                    Purpose = GoalTaskPurposes.Normalize(spec.Purpose),
                };
                var id = mutations.CreateJob(new CreateTaskRequest
                {
                    Title = spec.Title,
                    WatchPath = epic.WatchPath,
                    EpicId = epic.Id,
                    PromptMarkdown = spec.PromptMarkdown,
                    CliType = spec.CliType ?? epic.CliType,
                    Model = spec.Model ?? epic.Model,
                    TargetState = safeTargetState,
                }, provenance);

                if (id is not null)
                {
                    var info = mutations.FindCreatedJob(id, epic.WatchPath);
                    if (info is not null)
                    {
                        if (dependencies.Count > 0)
                        {
                            mutations.SetTaskReferences(id, new TaskReferences
                            {
                                DependsOn = dependencies
                                    .Select(dependency => createdByPlanId[dependency].Key!)
                                    .Where(key => !string.IsNullOrWhiteSpace(key))
                                    .ToList(),
                            }, epic.WatchPath);
                            info = mutations.FindCreatedJob(id, epic.WatchPath) ?? info;
                        }
                        if (!string.IsNullOrWhiteSpace(planId))
                            createdByPlanId[planId] = info;
                        created.Add(id);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(planId))
                processedPlanIds.Add(planId);
        }
        return created;
    }

    /// <summary>
    /// Clamp a decomposition target lane to the only two lanes a freshly minted
    /// sub-task may legitimately land in: <see cref="TaskStates.Backlog"/>
    /// (triage) or <see cref="TaskStates.Ready"/> (queued to run). A sub-task
    /// has never been worked, so any other target - above all
    /// <see cref="TaskStates.AutoReview"/>, the orchestrator's post-core review
    /// lane - is a category error: an unworked card there has no run to review
    /// and gets swept toward <see cref="TaskStates.Archive"/> without ever being
    /// touched (the ASS-693 / ASS-716 incident). Anything that is not exactly
    /// <c>2-ready</c> is forced to the conservative <c>0-backlog</c> triage lane,
    /// which auto-pickup never reaches into, so a mis-targeted decomposition can
    /// never run or vanish unvetted.
    /// </summary>
    public static string ClampTargetState(string? targetState)
        => string.Equals(targetState, TaskStates.Ready, StringComparison.Ordinal)
            ? TaskStates.Ready
            : TaskStates.Backlog;
}
