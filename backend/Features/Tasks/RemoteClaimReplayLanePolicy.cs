namespace AgentStudio.Tasks;

/// <summary>
/// Decides whether a durable remote claim can be replayed from the task's
/// current board lane. A claim response is safe only after the card and its
/// live RunAttempt converge on Progress.
/// </summary>
public static class RemoteClaimReplayLanePolicy
{
    public static RemoteClaimReplayLaneDecision Decide(string? taskState)
    {
        if (string.Equals(taskState, TaskStates.Progress, StringComparison.OrdinalIgnoreCase))
            return new RemoteClaimReplayLaneDecision(RemoteClaimReplayLaneAction.AlreadyConverged);

        if (string.Equals(taskState, TaskStates.Ready, StringComparison.OrdinalIgnoreCase))
            return new RemoteClaimReplayLaneDecision(RemoteClaimReplayLaneAction.RepairToProgress);

        return new RemoteClaimReplayLaneDecision(
            RemoteClaimReplayLaneAction.Refuse,
            $"The original claim task is in lane '{taskState ?? "unknown"}', not Ready or Progress.");
    }
}

public sealed record RemoteClaimReplayLaneDecision(
    RemoteClaimReplayLaneAction Action,
    string? Message = null);

public enum RemoteClaimReplayLaneAction
{
    AlreadyConverged,
    RepairToProgress,
    Refuse,
}
