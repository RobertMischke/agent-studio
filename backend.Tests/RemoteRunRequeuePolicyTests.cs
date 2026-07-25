using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteRunRequeuePolicyTests
{
    [Fact]
    public void Missing_heartbeat_inside_grace_is_not_requeued()
    {
        var decision = RemoteRunRequeuePolicy.Decide(new RemoteRunRequeueFacts(
            SecondsSinceLastAuthorityActivity: 45,
            GraceSeconds: 120,
            RunnerRespondedWithActiveSet: true,
            RunnerReportsTaskActive: false));

        Assert.Equal(RemoteRunRequeueAction.WaitForGrace, decision.Action);
    }

    [Fact]
    public void Grace_elapsed_without_runner_answer_is_not_requeued()
    {
        var decision = RemoteRunRequeuePolicy.Decide(new RemoteRunRequeueFacts(
            SecondsSinceLastAuthorityActivity: 180,
            GraceSeconds: 120,
            RunnerRespondedWithActiveSet: false,
            RunnerReportsTaskActive: false));

        Assert.Equal(RemoteRunRequeueAction.WaitForRunner, decision.Action);
    }

    [Fact]
    public void Grace_elapsed_but_runner_reports_active_is_not_requeued()
    {
        var decision = RemoteRunRequeuePolicy.Decide(new RemoteRunRequeueFacts(
            SecondsSinceLastAuthorityActivity: 180,
            GraceSeconds: 120,
            RunnerRespondedWithActiveSet: true,
            RunnerReportsTaskActive: true));

        Assert.Equal(RemoteRunRequeueAction.KeepProgress, decision.Action);
    }

    [Fact]
    public void Grace_elapsed_and_runner_confirms_inactive_requeues()
    {
        var decision = RemoteRunRequeuePolicy.Decide(new RemoteRunRequeueFacts(
            SecondsSinceLastAuthorityActivity: 180,
            GraceSeconds: 120,
            RunnerRespondedWithActiveSet: true,
            RunnerReportsTaskActive: false));

        Assert.Equal(RemoteRunRequeueAction.Requeue, decision.Action);
        Assert.Equal("remote-runner-confirmed-inactive", decision.ReasonCode);
    }
}
