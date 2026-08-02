using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostOrchestratorJournalTests
{
    [Fact]
    public void Accepted_work_queue_and_pending_report_survive_process_reopen()
    {
        using var temp = new JournalDirectory();
        var path = Path.Combine(temp.Path, "journal.json");
        var journal = new HostOrchestratorJournal(path);
        var first = Acceptance("TS-1", "task-1", "permit-1", "run-1", "lease-1", 1);
        var second = Acceptance("TS-2", "task-2", "permit-2", "run-2", "lease-2", 1);
        journal.Enqueue(first);
        journal.Enqueue(second);

        Assert.Equal("TS-1", journal.TryStartNext()!.Task.TaskKey);
        var prepared = journal.PrepareReport(
            "runner-a",
            "host-a",
            "instance-a",
            1,
            [new HostCapabilityDto("git-push", "ready")]);
        Assert.Equal(1, prepared.Sequence);
        Assert.Equal(new HostCapacityDto(1, 1, 1, 1, 0), prepared.Capacity);
        Assert.Equal(["TS-1", "TS-2"], prepared.Work.Select(item => item.TaskKey));
        Assert.Equal(2, prepared.PostProcessing.Count);
        Assert.All(prepared.PostProcessing, step => Assert.Equal("post-worktree-containment", step.StepId));
        Assert.Null(prepared.Work[0].QueuePosition);
        Assert.Null(prepared.Work[0].ProcessId);
        Assert.Equal(0, prepared.Work[1].QueuePosition);

        var reopened = new HostOrchestratorJournal(path);
        var replay = reopened.PrepareReport(
            "runner-a",
            "host-a",
            "instance-a",
            1,
            [new HostCapabilityDto("changed", "faulted")]);
        Assert.Equal(prepared.Sequence, replay.Sequence);
        Assert.Equal(prepared.ObservedAt, replay.ObservedAt);
        Assert.Equal("git-push", Assert.Single(replay.Capabilities).Kind);
        reopened.AcknowledgeReport(1);
        var withExternalOccupancy = reopened.PrepareReport(
            "runner-a", "host-a", "instance-a", 3, [], occupiedCapacity: 2);
        Assert.Equal(new HostCapacityDto(3, 3, 2, 1, 1), withExternalOccupancy.Capacity);
        reopened.AcknowledgeReport(withExternalOccupancy.Sequence);
        reopened.Complete("task-1");

        var reopenedAgain = new HostOrchestratorJournal(path);
        Assert.Equal("TS-2", reopenedAgain.TryStartNext()!.Task.TaskKey);
        var next = reopenedAgain.PrepareReport(
            "runner-a",
            "host-a",
            "instance-a",
            1,
            [new HostCapabilityDto("git-push", "ready")]);
        Assert.Equal(3, next.Sequence);
        Assert.Equal(1, next.Capacity.Active);
        Assert.Equal(0, next.Capacity.Queued);
    }

    private static WorkPermitAcceptanceDto Acceptance(
        string taskKey,
        string taskId,
        string permitId,
        string runId,
        string leaseId,
        long fence)
    {
        var now = DateTime.UtcNow;
        var task = new TaskDto(taskId, "project-a", taskKey, taskKey, "3-progress", 2, now, now, "prompt");
        var run = new RunDto(runId, taskId, "running", "runner-a", fence, now, now, null);
        var lease = new LeaseDto(leaseId, runId, taskId, "runner-a", "instance-a", fence, now, now.AddMinutes(2), "active");
        return new WorkPermitAcceptanceDto(
            "accepted",
            permitId,
            run,
            task,
            lease,
            lease.ExpiresAt,
            [new PostStepPlanDto($"step-{taskId}", runId, "post-worktree-containment", "runner-a", "available")]);
    }

    private sealed class JournalDirectory : IDisposable
    {
        public JournalDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "host-orchestrator-journal-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
