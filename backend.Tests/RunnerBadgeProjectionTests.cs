using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2003 coverage for the lokal-vs-remote runner-badge projection
/// (<see cref="TaskRunnerService.ProjectRunnerBadge"/>). A remote runner acquires
/// the task's run lease before it spawns its CLI, so the projected badge is how
/// the board card tells a card running on another host apart from a plain local
/// run — the operator's missing "Abgleich im Stable Board".
/// </summary>
public sealed class RunnerBadgeProjectionTests
{
    [Fact]
    public void NoLease_ProjectsNull()
    {
        Assert.Null(TaskRunnerService.ProjectRunnerBadge(null, "dev@host"));
    }

    [Fact]
    public void RemoteRunnerLease_IsFlaggedRemote_WithLeaseOwnerName()
    {
        var lease = Lease(runnerId: "agent-runner-01", runnerName: "agent-runner-01", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.NotNull(badge);
        Assert.True(badge!.IsRemote);
        Assert.Equal("agent-runner-01", badge.RunnerName);
        Assert.Equal("linux-host", badge.Hostname);
        Assert.Equal(7, badge.FencingToken);
    }

    [Fact]
    public void LeaseOwnedByTheLocalBackend_ReadsAsLocal()
    {
        var lease = Lease(runnerId: "dev@windows-host", runnerName: "dev@windows-host", host: "windows-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.NotNull(badge);
        Assert.False(badge!.IsRemote);
    }

    [Fact]
    public void BlankRunnerName_FallsBackToTheRunnerId()
    {
        var lease = Lease(runnerId: "agent-runner-02", runnerName: "", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.Equal("agent-runner-02", badge!.RunnerName);
    }

    [Fact]
    public void BlankLocalIdentity_CannotProveLocal_SoAHeldLeaseReadsAsRemote()
    {
        var lease = Lease(runnerId: "agent-runner-01", runnerName: "agent-runner-01", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: null);

        Assert.True(badge!.IsRemote);
    }

    // End-to-end against the real lease authority: a remote runner acquires the
    // run lease, Peek surfaces it, and the projection turns it into a remote badge.
    [Fact]
    public void AcquiredRemoteLease_PeekedAndProjected_YieldsRemoteBadge()
    {
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance);
        leases.TryAcquire(new RunLeaseAcquireRequest(
            "PT-578", "agent-runner-01", "agent-runner-01", "linux-host", 4321, "remote"));

        var peek = leases.Peek("PT-578");
        var badge = TaskRunnerService.ProjectRunnerBadge(peek.Lease, localRunnerId: "stable@windows-host");

        Assert.Equal("Held", peek.Outcome);
        Assert.NotNull(badge);
        Assert.True(badge!.IsRemote);
        Assert.Equal("agent-runner-01", badge.RunnerName);
    }

    private static RunLeaseInfoDto Lease(string runnerId, string runnerName, string host)
        => new(
            TaskKey: "PT-578",
            RunnerId: runnerId,
            RunnerName: runnerName,
            Hostname: host,
            Pid: 4321,
            BackendName: "remote",
            LeaseId: "lease-abc",
            FencingToken: 7,
            AcquiredAt: new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            ExpiresAt: new DateTime(2026, 7, 9, 10, 2, 0, DateTimeKind.Utc));
}
