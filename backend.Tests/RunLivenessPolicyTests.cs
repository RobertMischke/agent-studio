using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pure decision core for Run-Liveness Slice A. Locks the whole invariant
/// (concept Rule 4, "no zombie survives 60s") with fixture cases so the
/// runner-side executor only has to gather the facts:
///
/// <list type="bullet">
///   <item>a live run-heartbeat is always Healthy, whatever the silence;</item>
///   <item>no heartbeat but inside the grace -> WithinGrace (re-checked next tick);</item>
///   <item>no heartbeat past the grace + run never finished -> DemoteToReady (process-lost);</item>
///   <item>no heartbeat past the grace + run finished -> RetriggerPostProcessing (post-processing-lost).</item>
/// </list>
/// </summary>
public sealed class RunLivenessPolicyTests
{
    [Fact]
    public void LiveHeartbeat_IsHealthy_EvenWhenSilentAndFinished()
    {
        // A live run-heartbeat trumps everything: a healthy post-processing card
        // has no CLI process and can be silent for minutes, but its owning run
        // still holds it. Must never be demoted.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: true,
            CoreRunFinished: true,
            SecondsSinceActivity: 9999,
            GraceSeconds: 0));

        Assert.Equal(RunLivenessAction.Healthy, d.Action);
        Assert.Equal(RunLivenessReasons.HeartbeatPresent, d.ReasonCode);
    }

    [Fact]
    public void NoHeartbeat_WithinGrace_IsDeferred()
    {
        // A card that just went heartbeat-less is not judged yet: during uptime
        // there is a beat between the lane move and the run claim.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: 10,
            GraceSeconds: 30));

        Assert.Equal(RunLivenessAction.WithinGrace, d.Action);
        Assert.Equal(RunLivenessReasons.WithinGrace, d.ReasonCode);
    }

    [Fact]
    public void NoHeartbeat_PastGrace_ExecutionInterrupted_DemotesWithProcessLost()
    {
        // Belegt AGT-2006: the run process is gone and the core run never
        // finished. Demote to 2-ready so a fresh run retries the same task.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: 45,
            GraceSeconds: 30));

        Assert.Equal(RunLivenessAction.DemoteToReady, d.Action);
        Assert.Equal(RunLivenessReasons.ProcessLost, d.ReasonCode);
    }

    [Fact]
    public void NoHeartbeat_PastGrace_RunFinished_RetriggersPostProcessing()
    {
        // Belegt AGT-1932: the run finished (and was merged) and only
        // post-processing died with the backend. Re-trigger post-processing
        // rather than re-running the completed agent.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: true,
            SecondsSinceActivity: 45,
            GraceSeconds: 30));

        Assert.Equal(RunLivenessAction.RetriggerPostProcessing, d.Action);
        Assert.Equal(RunLivenessReasons.PostProcessingLost, d.ReasonCode);
    }

    [Fact]
    public void BootGraceZero_DemotesImmediately_WhenSilentFromTheStart()
    {
        // Boot adoption uses grace = 0 so a run that crashed and never logged
        // again is adopted at once, even if only seconds have passed.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: 1,
            GraceSeconds: 0));

        Assert.Equal(RunLivenessAction.DemoteToReady, d.Action);
        Assert.Equal(RunLivenessReasons.ProcessLost, d.ReasonCode);
    }

    [Fact]
    public void GraceBoundary_IsInclusiveOfDemotion_AtExactlyGrace()
    {
        // Silence == grace is past the window (>= budget), so the card is acted
        // on rather than deferred forever at the boundary.
        var d = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: 30,
            GraceSeconds: 30));

        Assert.Equal(RunLivenessAction.DemoteToReady, d.Action);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(3600)]
    public void NoHeartbeat_PastSixtySeconds_IsOnlyLegalWithVisibleWait(double seconds)
    {
        var zombie = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: seconds,
            GraceSeconds: 30,
            HasVisibleWaitingState: false));
        var visibleWait = RunLivenessPolicy.Decide(new RunLivenessFacts(
            HasLiveRunHeartbeat: false,
            CoreRunFinished: false,
            SecondsSinceActivity: seconds,
            GraceSeconds: 30,
            HasVisibleWaitingState: true));

        Assert.Equal(RunLivenessAction.DemoteToReady, zombie.Action);
        Assert.Equal(RunLivenessAction.VisibleWait, visibleWait.Action);
        Assert.Equal(RunLivenessReasons.VisibleWait, visibleWait.ReasonCode);
    }
}
