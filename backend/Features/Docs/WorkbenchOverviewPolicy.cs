namespace AgentStudio.Docs;

/// <summary>
/// Deterministic ordering for the workspace-wide and project-scoped Workbench
/// queues. Keeping the comparison free of registry, filesystem, and clock
/// dependencies makes the operator-priority rules directly testable.
/// </summary>
public static class WorkbenchOverviewPolicy
{
    public static List<WorkbenchOverviewItem> Sort(IEnumerable<WorkbenchOverviewItem> items) =>
        items
            .OrderBy(item => SectionRank(item.Workbench.Status))
            .ThenByDescending(item => item.Workbench.Status == "decision-pending"
                ? item.Workbench.OpenDecisionCount
                : 0)
            .ThenByDescending(item => item.Workbench.UpdatedAtUtc)
            .ThenBy(item => item.Workbench.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int SectionRank(string status) => status switch
    {
        "decision-pending" => 0,
        "active" => 1,
        "invalid" => 2,
        "archived" => 3,
        "decided" => 4,
        _ => 5,
    };
}
