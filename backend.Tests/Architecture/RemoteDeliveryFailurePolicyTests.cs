using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteDeliveryFailurePolicyTests
{
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, true)]
    public void Consecutive_attempt_matrix_requeues_once_then_escalates(
        int previousFailures,
        int expectedAttempt,
        bool expectedEscalation)
    {
        var decision = RemoteDeliveryFailurePolicy.Decide(previousFailures);

        Assert.Equal(expectedAttempt, decision.Attempt);
        Assert.Equal(2, decision.MaximumAttempts);
        Assert.Equal(expectedEscalation, decision.Escalate);
    }

    [Fact]
    public void Durable_state_survives_the_first_retry_and_resets_after_operator_requeue()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "remote-delivery-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var task = new TaskInfo { Id = "AGT-1", FolderPath = folder };
        var store = NewStore();

        try
        {
            var first = store.Record(
                task,
                "missing BaseSha",
                "agent-studio/salvage/runner/AGT-1/run/fence-1/abc",
                new string('a', 40));
            store.PrepareForClaim(task);
            var second = store.Record(task, "missing BaseSha", null, null);

            Assert.False(first.Escalate);
            Assert.True(second.Escalate);
            Assert.Equal(2, RemoteDeliveryFailureStore.Read(folder)!.ConsecutiveAttempts);

            store.PrepareForClaim(task);

            Assert.Null(RemoteDeliveryFailureStore.Read(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static RemoteDeliveryFailureStore NewStore()
        => new(NullLogger<RemoteDeliveryFailureStore>.Instance);
}
