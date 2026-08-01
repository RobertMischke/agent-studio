namespace AgentStudio.Shared;

/// <summary>
/// Stable pipeline configuration types. They deliberately sit above the
/// task's persisted <see cref="TaskInfo.TaskType"/> and
/// <see cref="TaskInfo.Mode"/> fields: coding chores use <see cref="Task"/>,
/// bugs and features keep their structural type, while every read-only mode
/// uses the lightweight <see cref="Planning"/> chain.
///
/// Keeping this vocabulary separate makes the settings model extensible
/// without overloading either the card kind or execution mode taxonomies.
/// </summary>
public static class PipelineTypes
{
    public const string Task = "task";
    public const string Bug = "bug";
    public const string Feature = "feature";
    public const string Planning = "planning";

    public static readonly string[] All = [Task, Bug, Feature, Planning];
    public static readonly string[] LegacyCodingTypes = [Task, Bug, Feature];

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Task;
        foreach (var type in All)
            if (string.Equals(type, value.Trim(), StringComparison.OrdinalIgnoreCase))
                return type;
        return Task;
    }

    /// <summary>
    /// Resolve the configurable pipeline type from the card. Report-only intent
    /// wins over the structural task type so planning/research can never acquire
    /// git steps. Concept keeps its dedicated catalogue outside this settings
    /// dimension.
    /// </summary>
    public static string Resolve(string? taskType, string? mode)
    {
        if (TaskModes.IsReportOnly(mode)) return Planning;
        return TaskTypes.Normalize(taskType) switch
        {
            TaskTypes.Bug => Bug,
            TaskTypes.Feature => Feature,
            _ => Task,
        };
    }

    public static string Resolve(TaskInfo task) => Resolve(task.TaskType, task.Mode);
}
