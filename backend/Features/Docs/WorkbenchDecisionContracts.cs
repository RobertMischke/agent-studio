namespace AgentStudio.Docs;

/// <summary>
/// The shared, dependency-free contract of a Workbench decision (AGT-2375).
/// Both the read side (<see cref="WorkbenchCatalogueService"/>, when it projects
/// a stored <c>decision</c> receipt) and the write side
/// (<see cref="WorkbenchDecisionService"/>) validate through exactly these
/// rules, so a receipt that was accepted on write can never be rejected on the
/// next read.
/// </summary>
public static class WorkbenchDecisionContracts
{
    /// <summary>
    /// Operation ids are client-generated idempotency keys. They are echoed into
    /// the durable receipt, so they must stay filename- and log-safe.
    /// </summary>
    public static bool SafeOperationId(string? value) =>
        value is { Length: >= 8 and <= 128 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>
    /// Lanes a decision draft may target. A Workbench decision hands work to the
    /// intake, never straight into execution, so the draft cannot name a running
    /// or reviewing lane.
    /// </summary>
    private static readonly string[] AllowedInitialLanes =
        [TaskStates.Backlog, TaskStates.Preparation];

    /// <summary>
    /// Returns null when the draft is a well-formed task request, otherwise a
    /// single human-readable reason. The draft is only ever a *request*: the
    /// decision service never creates the card itself (the client owns creation
    /// through the existing task API), so validation here is about bounding the
    /// text that gets written into the durable receipt.
    /// </summary>
    public static string? ValidateTaskDraft(WorkbenchTaskDraft? draft)
    {
        if (draft == null)
            return "task draft is missing.";
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 240)
            return "task.title is required and must be at most 240 characters.";
        if (string.IsNullOrWhiteSpace(draft.Goal) || draft.Goal.Trim().Length > 20_000)
            return "task.goal is required and must be at most 20000 characters.";
        if (draft.AcceptanceCriteria.Count is 0 or > 100
            || draft.AcceptanceCriteria.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.acceptanceCriteria needs 1-100 non-empty bounded items.";
        if (draft.EvidenceLinks.Count > 100
            || draft.EvidenceLinks.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.evidenceLinks contains an invalid item.";
        if (draft.RelatedTaskKeys.Count > 100
            || draft.RelatedTaskKeys.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 100))
            return "task.relatedTaskKeys contains an invalid item.";
        if (draft.ChosenOption is { Length: > 2000 })
            return "task.chosenOption is too long.";
        if (!AllowedInitialLanes.Contains(draft.InitialLane, StringComparer.Ordinal))
            return "task.initialLane is invalid.";
        if (!TaskModes.IsValid(draft.Mode))
            return "task.mode is invalid.";
        if (!TaskTypes.All.Contains(draft.TaskType, StringComparer.Ordinal))
            return "task.taskType is invalid.";
        return null;
    }

    /// <summary>
    /// Bounds the repository-authored decision ids and the operator text that
    /// will be stored in the Git-backed receipt. The browser additionally
    /// checks every id against the inertly parsed HTML convention.
    /// </summary>
    public static string? ValidateAnswers(IReadOnlyList<WorkbenchDecisionAnswer>? answers)
    {
        if (answers == null)
            return "answers must be an array.";
        if (answers.Count > 100)
            return "answers may contain at most 100 decision points.";
        var decisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (!SafeMarkupId(answer.DecisionId) || !decisionIds.Add(answer.DecisionId))
                return "answers contain an invalid or duplicate decisionId.";
            if (answer.Kind is not ("single" or "multi" or "confirm"))
                return $"answer '{answer.DecisionId}' has an invalid kind.";
            if (answer.SelectedOptions.Count is 0 or > 100)
                return $"answer '{answer.DecisionId}' must select at least one bounded option.";
            if (answer.Kind is "single" or "confirm" && answer.SelectedOptions.Count != 1)
                return $"answer '{answer.DecisionId}' must select exactly one option.";
            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in answer.SelectedOptions)
            {
                if (!SafeMarkupId(option.Id) || !optionIds.Add(option.Id)
                    || string.IsNullOrWhiteSpace(option.Label) || option.Label.Trim().Length > 2_000)
                    return $"answer '{answer.DecisionId}' contains an invalid option.";
            }
            if (answer.Comment is { Length: > 20_000 })
                return $"answer '{answer.DecisionId}' comment is too long.";
        }
        return null;
    }

    private static bool SafeMarkupId(string? value) =>
        value is { Length: >= 1 and <= 120 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed record WorkbenchDecisionOption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed record WorkbenchDecisionAnswer
{
    public string DecisionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<WorkbenchDecisionOption> SelectedOptions { get; init; } = [];
    public string? Comment { get; init; }
}

/// <summary>
/// The task a feature decision asks for. It is stored verbatim in the decision
/// receipt and handed back to the caller; the backend does not turn it into a
/// card (see <see cref="WorkbenchDecisionService"/>).
/// </summary>
public sealed record WorkbenchTaskDraft
{
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public List<string> AcceptanceCriteria { get; init; } = [];
    public List<string> EvidenceLinks { get; init; } = [];
    public string? ChosenOption { get; init; }
    public List<string> RelatedTaskKeys { get; init; } = [];
    public string? TargetProject { get; init; }
    public string InitialLane { get; init; } = TaskStates.Preparation;
    public string Mode { get; init; } = TaskModes.Coding;
    public string TaskType { get; init; } = TaskTypes.Feature;
}
