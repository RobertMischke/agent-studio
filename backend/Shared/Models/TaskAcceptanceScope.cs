namespace AgentStudio.Shared;

/// <summary>
/// Stable values for <see cref="TaskAcceptanceScope.DeliveryMode"/>.
/// A missing scope keeps the legacy full-card interpretation. A bounded slice
/// explicitly limits requirement-fit review to one independently deliverable
/// unit even when the prompt links to a broader Dossier or recommendation list.
/// </summary>
public static class TaskAcceptanceDeliveryModes
{
    public const string FullTask = "full-task";
    public const string BoundedSlice = "bounded-slice";
}

/// <summary>
/// Application-owned acceptance boundary persisted in <c>task.json</c> as
/// <c>acceptanceScope</c>. The reviewer treats <see cref="Slice"/> and every
/// item in <see cref="Criteria"/> as the complete requirement-fit boundary.
/// Requirements outside that boundary remain parent/Dossier backlog, not a
/// reason to block this delivery.
/// </summary>
public sealed record TaskAcceptanceScope
{
    public string DeliveryMode { get; init; } = TaskAcceptanceDeliveryModes.FullTask;
    public string? Slice { get; init; }
    public List<string> Criteria { get; init; } = [];
}

public static class TaskAcceptanceScopes
{
    public static TaskAcceptanceScope? Normalize(TaskAcceptanceScope? value)
    {
        if (value is null) return null;
        var mode = string.Equals(
            value.DeliveryMode?.Trim(),
            TaskAcceptanceDeliveryModes.BoundedSlice,
            StringComparison.OrdinalIgnoreCase)
                ? TaskAcceptanceDeliveryModes.BoundedSlice
                : string.Equals(
                    value.DeliveryMode?.Trim(),
                    TaskAcceptanceDeliveryModes.FullTask,
                    StringComparison.OrdinalIgnoreCase)
                    ? TaskAcceptanceDeliveryModes.FullTask
                    : null;
        if (mode is null) return null;

        var criteria = (value.Criteria ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var slice = string.IsNullOrWhiteSpace(value.Slice) ? null : value.Slice.Trim();
        if (mode == TaskAcceptanceDeliveryModes.BoundedSlice
            && (slice is null || criteria.Count == 0))
            return null;

        return new TaskAcceptanceScope
        {
            DeliveryMode = mode,
            Slice = slice,
            Criteria = criteria,
        };
    }

    public static TaskAcceptanceScope BoundedSlice(string slice, params string[] criteria)
        => Normalize(new TaskAcceptanceScope
        {
            DeliveryMode = TaskAcceptanceDeliveryModes.BoundedSlice,
            Slice = slice,
            Criteria = criteria.ToList(),
        }) ?? throw new ArgumentException("A bounded acceptance scope requires a slice name and at least one criterion.");
}
