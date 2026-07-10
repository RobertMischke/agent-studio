namespace AgentStudio.Shared;

/// <summary>
/// PUB-1 read-time projection folded onto <see cref="TaskInfo.PublishSignal"/>:
/// which publish targets an accepted (6-completed) task's merged work is
/// publishable to, so the card / task-detail renders a "publishable: npm,
/// website" chip. Built batched per project by <c>TaskPublishableService</c> via
/// set-membership of the task's mainline anchor against each target's pending
/// commit set (O(projects), never per card). Kept in <c>AgentStudio.Shared</c>
/// alongside the other read-time projections (<see cref="TaskMergeSignal"/>,
/// <see cref="WaitsOnStatus"/>) so <see cref="TaskInfo"/>'s field types stay
/// within Shared. Never persisted to <c>task.json</c>.
/// </summary>
public record TaskPublishSignal
{
    /// <summary>Target ids the task is publishable to (<c>package:npm</c>, <c>website</c>, ...).</summary>
    public List<string> TargetIds { get; init; } = [];

    /// <summary>Short labels for the chip, in target order (e.g. <c>npm</c>, <c>Website</c>).</summary>
    public List<string> Labels { get; init; } = [];
}
