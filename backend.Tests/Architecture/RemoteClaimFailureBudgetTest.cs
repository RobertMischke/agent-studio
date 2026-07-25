using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture-level breaker for the loop-inventory entry
/// <c>remote-claim.environment-preparation-per-task</c>.
/// </summary>
public sealed class RemoteClaimFailureBudgetTest
{
    [Fact]
    public void Persistent_budget_allows_two_requeues_then_escalates()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "remote-claim-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var task = new TaskInfo { Id = "AGT-1", FolderPath = folder };

        try
        {
            Assert.Equal(3, RemoteClaimFailureBudget.MaxAttempts);

            var first = NewBudget().Record(task, "clone failed: 403 agent-orc/website");
            var second = NewBudget().Record(task, "clone failed: 403 agent-orc/website");
            var third = NewBudget().Record(task, "clone failed: 403 agent-orc/website");

            Assert.Equal(1, first.Attempt);
            Assert.False(first.Escalate);
            Assert.Equal(2, second.Attempt);
            Assert.False(second.Escalate);
            Assert.Equal(3, third.Attempt);
            Assert.True(third.Escalate);

            NewBudget().PrepareForClaim(task);
            var afterOperatorRequeue = NewBudget().Record(task, "clone failed again");
            Assert.Equal(1, afterOperatorRequeue.Attempt);
            Assert.False(afterOperatorRequeue.Escalate);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static RemoteClaimFailureBudget NewBudget()
        => new(NullLogger<RemoteClaimFailureBudget>.Instance);
}
