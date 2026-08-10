using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchOverviewPolicyTests
{
    [Fact]
    public void Sort_PrioritizesOpenDecisionCountThenActiveRecencyAndSeparatesHistory()
    {
        var items = new[]
        {
            Item("Project B", "active-new", "active", "2026-08-09T11:00:00Z"),
            Item("Project A", "pending-one", "decision-pending", "2026-08-09T12:00:00Z", 1),
            Item("Project A", "archived", "archived", "2026-08-09T13:00:00Z"),
            Item("Project B", "active-old", "active", "2026-08-08T11:00:00Z"),
            Item("Project B", "pending-four", "decision-pending", "2026-08-08T12:00:00Z", 4),
            Item("Project A", "decided", "decided", "2026-08-09T14:00:00Z"),
            Item("Project A", "documented", "documented", "2026-08-09T15:00:00Z"),
        };

        var sorted = WorkbenchOverviewPolicy.Sort(items);

        Assert.Equal(
            ["pending-four", "pending-one", "active-new", "active-old", "decided", "archived", "documented"],
            sorted.Select(item => item.Workbench.Id));
    }

    private static WorkbenchOverviewItem Item(
        string project,
        string id,
        string status,
        string updatedAt,
        int openDecisionCount = 0) =>
        new(project, new WorkbenchListItem(
            id,
            id,
            $"{id} summary",
            status,
            "testing",
            DateTime.Parse(updatedAt).ToUniversalTime(),
            $"docs/{id}/index.html",
            true,
            null,
            [])
        {
            OpenDecisionCount = openDecisionCount,
        });
}
