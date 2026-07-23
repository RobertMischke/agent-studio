using AgentStudio.Pipeline;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteGateActivityStoreTests
{
    [Fact]
    public void ForRunner_ProjectsStartedAndCompletedGateEventsBySshAlias()
    {
        var store = new RemoteGateActivityStore();
        var now = DateTimeOffset.UtcNow;

        store.Started("gate-a", "agent-runner", now);
        store.Started("gate-b", "agent-runner", now.AddSeconds(1));
        store.Started("gate-other", "runner-berlin", now);

        var active = store.ForRunner("agent-runner-01");

        Assert.Equal(2, active.Active);
        Assert.Equal(RemoteGateActivityStore.Capacity, active.Capacity);
        Assert.Equal(["gate-a", "gate-b"], active.Gates.Select(gate => gate.GateRunId));

        store.Completed("gate-a");

        Assert.Equal(1, store.ForRunner("agent-runner-01").Active);
        Assert.Equal(1, store.ForRunner("runner-berlin-02").Active);
    }
}
