namespace AgentStudio.Shared;

/// <summary>
/// Rollup view of one epic + its sub-tasks for <c>GET /api/epics</c>. The epic
/// is a <see cref="TaskKinds.Epic"/> card; sub-tasks are the tasks whose
/// <see cref="TaskInfo.EpicId"/> points at it. Progress is derived live from
/// the sub-tasks' lanes so the board can show "3 / 7 done" without a separate
/// store.
/// </summary>
public sealed record EpicRollup(
    string Id,
    string? Key,
    string Title,
    string ProjectName,
    string WatchPath,
    string State,
    int SubTaskTotal,
    int Completed,
    int InProgress,
    int Open,
    DateTime? CompletedAt,
    IReadOnlyDictionary<string, int> ByState,
    IReadOnlyList<EpicSubTaskRef> SubTasks);

/// <summary>One sub-task reference inside an <see cref="EpicRollup"/>.</summary>
public sealed record EpicSubTaskRef(
    string Id,
    string Title,
    string State,
    int Order,
    string? OrchestratorVerdict = null);

/// <summary>
/// Body for <c>POST /api/epics/{id}/sub-tasks</c> (assignment way 3, the
/// deterministic half): create one or more sub-tasks under an epic. An epic's
/// decomposition/planning run produces this list; the LLM that generates the
/// titles/prompts is the orchestrator side and is wired separately.
/// </summary>
public sealed record CreateEpicSubTasksRequest(IReadOnlyList<EpicSubTaskSpec> SubTasks);

/// <summary>
/// One goal-decomposition node. <see cref="PlanId"/> is local to the plan and
/// lets <see cref="DependsOn"/> describe a DAG before stable task keys exist.
/// <see cref="Purpose"/> distinguishes delivery work from an independently
/// scheduled verification task.
/// </summary>
public sealed record EpicSubTaskSpec(
    string Title,
    string? PromptMarkdown = null,
    string? CliType = null,
    string? Model = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? PlanId = null,
    IReadOnlyList<string>? DependsOn = null,
    string? Purpose = null);

/// <summary>Pure validation result for an epic goal-decomposition plan.</summary>
public sealed record EpicGoalPlanValidation(bool IsValid, string? Error)
{
    public static readonly EpicGoalPlanValidation Valid = new(true, null);
}

/// <summary>
/// Validates the local dependency graph before any cards are created. Plans
/// without dependency metadata remain backwards compatible. Once a task uses
/// <c>dependsOn</c>, every referenced node must have a unique plan id and the
/// graph must remain acyclic.
/// </summary>
public static class EpicGoalPlanValidator
{
    public static EpicGoalPlanValidation Validate(IReadOnlyList<EpicSubTaskSpec>? specs)
    {
        if (specs is null || specs.Count == 0) return EpicGoalPlanValidation.Valid;

        var byId = new Dictionary<string, EpicSubTaskSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.PlanId)) continue;
            var id = spec.PlanId.Trim();
            if (!byId.TryAdd(id, spec))
                return new(false, $"duplicate plan id '{id}'");
        }

        foreach (var spec in specs)
        {
            foreach (var dependency in NormalizeDependencies(spec.DependsOn))
            {
                if (string.IsNullOrWhiteSpace(spec.PlanId))
                    return new(false, $"task '{spec.Title}' uses dependsOn but has no plan id");
                if (!byId.ContainsKey(dependency))
                    return new(false, $"task '{spec.PlanId}' depends on unknown plan id '{dependency}'");
                if (string.Equals(spec.PlanId.Trim(), dependency, StringComparison.OrdinalIgnoreCase))
                    return new(false, $"task '{spec.PlanId}' depends on itself");
            }
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string id)
        {
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            var node = byId[id];
            foreach (var dependency in NormalizeDependencies(node.DependsOn))
            {
                if (!Visit(dependency)) return false;
            }
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        foreach (var id in byId.Keys)
        {
            if (!Visit(id))
                return new(false, $"dependsOn contains a cycle involving plan id '{id}'");
        }

        return EpicGoalPlanValidation.Valid;
    }

    internal static IReadOnlyList<string> NormalizeDependencies(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
