using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class IntegrationLeaseServiceTests
{
    [Fact]
    public void TryAcquire_QueuesContenderUntilCurrentHolderReleases()
    {
        var service = NewService();
        var first = Request("task-a", "runner-a");
        var second = Request("task-b", "runner-b");

        var acquired = service.TryAcquire(first);
        var queued = service.TryAcquire(second);

        Assert.True(acquired.Granted);
        Assert.Equal("Acquired", acquired.Outcome);
        Assert.Equal(1, acquired.Lease!.FencingToken);
        Assert.False(queued.Granted);
        Assert.Equal("Queued", queued.Outcome);
        Assert.Equal(1, queued.QueuePosition);
        Assert.Equal("task-a", queued.Lease!.TaskKey);

        var released = service.Release(new IntegrationLeaseReleaseRequest(
            acquired.Lease.ProjectName,
            acquired.Lease.IntegrationBranch,
            acquired.Lease.LeaseId,
            acquired.Lease.FencingToken,
            acquired.Lease.RunnerId));
        var secondAcquire = service.TryAcquire(second);

        Assert.Equal("Released", released.Outcome);
        Assert.True(secondAcquire.Granted);
        Assert.Equal("Acquired", secondAcquire.Outcome);
        Assert.Equal("task-b", secondAcquire.Lease!.TaskKey);
        Assert.Equal(2, secondAcquire.Lease.FencingToken);
    }

    [Fact]
    public void TryAcquire_DifferentIntegrationBranchesDoNotBlockEachOther()
    {
        var service = NewService();

        var develop = service.TryAcquire(Request("task-a", "runner-a", branch: "develop"));
        var release = service.TryAcquire(Request("task-b", "runner-b", branch: "release/1.0"));

        Assert.True(develop.Granted);
        Assert.True(release.Granted);
        Assert.Equal("develop", develop.Lease!.IntegrationBranch);
        Assert.Equal("release/1.0", release.Lease!.IntegrationBranch);
    }

    [Fact]
    public void TryAcquire_SameOwnerIsIdempotent()
    {
        var service = NewService();
        var request = Request("task-a", "runner-a");

        var first = service.TryAcquire(request);
        var again = service.TryAcquire(request);

        Assert.True(first.Granted);
        Assert.True(again.Granted);
        Assert.Equal("AlreadyOwn", again.Outcome);
        Assert.Equal(first.Lease!.LeaseId, again.Lease!.LeaseId);
        Assert.Equal(first.Lease.FencingToken, again.Lease.FencingToken);
    }

    [Fact]
    public void ExpiredLease_AllowsHigherFencedAcquire_AndRejectsStaleHeartbeat()
    {
        var now = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.TryAcquire(Request("task-a", "runner-a", ttlSeconds: 30));
        Assert.True(first.Granted);

        now = now.AddSeconds(31);
        var second = service.TryAcquire(Request("task-b", "runner-b", ttlSeconds: 30));
        var staleHeartbeat = service.Renew(new IntegrationLeaseHeartbeatRequest(
            first.Lease!.ProjectName,
            first.Lease.IntegrationBranch,
            first.Lease.LeaseId,
            first.Lease.FencingToken,
            first.Lease.RunnerId,
            RequestedTtlSeconds: 30));

        Assert.True(second.Granted);
        Assert.Equal(2, second.Lease!.FencingToken);
        Assert.False(staleHeartbeat.Granted);
        Assert.Equal("StaleToken", staleHeartbeat.Outcome);
        Assert.Equal("task-b", staleHeartbeat.Lease!.TaskKey);
    }

    private static IntegrationLeaseService NewService(Func<DateTime>? utcNow = null)
        => new(NullLogger<IntegrationLeaseService>.Instance, utcNow);

    private static IntegrationLeaseAcquireRequest Request(
        string task,
        string runner,
        string project = "agent-taskboard",
        string branch = "develop",
        int ttlSeconds = 600)
        => new(
            project,
            branch,
            task,
            runner,
            "host-a",
            1234,
            "stable",
            ttlSeconds);
}
