using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RunningTaskCountTests
{
    [Fact]
    public void LocalProgressCard_IsCounted()
    {
        var local = ProgressTask("local", "ATP-1", "alpha");

        var counts = TaskRunnerService.CountRunningTasksByProject(
            [local],
            task => task.TaskKey == local.TaskKey,
            _ => false);

        Assert.Equal(1, counts["alpha"]);
    }

    [Fact]
    public void RemoteProgressCard_WithActiveLease_IsCounted()
    {
        var remote = ProgressTask("remote", "ATP-2", "alpha");
        var leases = ActiveLeases(remote.TaskKey);

        var counts = TaskRunnerService.CountRunningTasksByProject(
            [remote],
            _ => false,
            taskKey => leases.Peek(taskKey).Lease is not null);

        Assert.Equal(1, counts["alpha"]);
    }

    [Fact]
    public void RemoteProgressCard_WithExpiredLease_IsNotCounted()
    {
        var now = new DateTime(2026, 7, 25, 8, 0, 0, DateTimeKind.Utc);
        var remote = ProgressTask("remote", "ATP-2", "alpha");
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance, () => now);
        leases.TryAcquire(new RunLeaseAcquireRequest(
            remote.TaskKey,
            "remote-runner",
            "Remote Runner",
            "remote-host",
            42,
            "remote",
            RequestedTtlSeconds: 30));
        now = now.AddSeconds(31);

        var counts = TaskRunnerService.CountRunningTasksByProject(
            [remote],
            _ => false,
            taskKey => leases.Peek(taskKey).Lease is not null);

        Assert.Empty(counts);
    }

    [Fact]
    public void LocalAndRemoteProgressCards_AreCountedOnceEach()
    {
        var local = ProgressTask("local", "ATP-1", "alpha");
        var remote = ProgressTask("remote", "ATP-2", "alpha");
        var locallyLeased = ProgressTask("local-leased", "ATP-3", "beta");
        var staleLaneLease = ProgressTask("review", "ATP-4", "beta") with
        {
            State = TaskStates.AutoReview,
        };
        var leases = ActiveLeases(remote.TaskKey, locallyLeased.TaskKey, staleLaneLease.TaskKey);

        var counts = TaskRunnerService.CountRunningTasksByProject(
            [local, remote, locallyLeased, staleLaneLease],
            task => task.TaskKey is "ATP-1" or "ATP-3",
            taskKey => leases.Peek(taskKey).Lease is not null);

        Assert.Equal(2, counts["alpha"]);
        Assert.Equal(1, counts["beta"]);
        Assert.Equal(3, counts.Values.Sum());
    }

    private static TaskInfo ProgressTask(string id, string taskKey, string project) => new()
    {
        Id = id,
        TaskKey = taskKey,
        ProjectName = project,
        State = TaskStates.Progress,
    };

    private static RunLeaseService ActiveLeases(params string[] taskKeys)
    {
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance);
        foreach (var taskKey in taskKeys)
        {
            leases.TryAcquire(new RunLeaseAcquireRequest(
                taskKey,
                "remote-runner",
                "Remote Runner",
                "remote-host",
                42,
                "remote"));
        }
        return leases;
    }
}
