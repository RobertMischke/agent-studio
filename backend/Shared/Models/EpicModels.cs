namespace OrchestratorApi.Models;

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

public sealed record EpicSubTaskSpec(string Title, string? PromptMarkdown = null, string? CliType = null, string? Model = null);
