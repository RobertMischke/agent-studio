using AgentStudio.Pipeline;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure acceptance policy. Coding deliveries require a successful integration
/// outcome before they may remain in Completed. Explicitly branch-less cards
/// and a one-shot operator override are the only exemptions.
/// </summary>
public static class AcceptanceIntegrationPolicy
{
    public static bool IsIntegrationRequired(TaskInfo task)
    {
        if (task.NoBranchExpected
            || TaskModes.IsReadOnly(task.Mode)
            || TaskKinds.IsEpic(task.Kind))
        {
            return false;
        }

        return !IsNoBranchTaskType(task.TaskType);
    }

    public static bool IsNoBranchTaskType(string? taskType) =>
        string.Equals(taskType, "concept", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskType, "decision", StringComparison.OrdinalIgnoreCase);

    public static AcceptedIntegrationLaneDecision Decide(
        MergeIntoIntegrationOutcome outcome,
        bool operatorOverride = false,
        bool integrationRequired = true)
    {
        if (operatorOverride || !integrationRequired)
            return AcceptedIntegrationLaneDecision.Complete;
        if (outcome == MergeIntoIntegrationOutcome.GateEnvironmentBlocked)
            return AcceptedIntegrationLaneDecision.RetryLater;

        return outcome.IsSuccessfulIntegration()
            ? AcceptedIntegrationLaneDecision.Complete
            : AcceptedIntegrationLaneDecision.ReturnToHumanReview;
    }
}

public enum AcceptedIntegrationLaneDecision
{
    Complete,
    ReturnToHumanReview,
    /// <summary>
    /// The attempt reached no verdict about the delivery because the gate host
    /// failed. The card keeps its lane and the accepted-integration backstop
    /// replays the merge; sending it to Human Review would ask an operator to
    /// judge work that was never tested.
    /// </summary>
    RetryLater,
}
