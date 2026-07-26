using Xunit;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Tests;

/// <summary>
/// Pure decision core for Run-Liveness Slice B (concept Rule 2, "an unanswered
/// steer never waits indefinitely"). Locks the whole invariant with fixture
/// cases so the monitor only has to gather facts + resolve the answer:
///
/// <list type="bullet">
///   <item>inside the timeout -> KeepWaiting (re-checked next sweep);</item>
///   <item>timed out + an unambiguous answer -> AutoAnswer;</item>
///   <item>timed out + no confident answer -> RouteBlocked (never an endless wait).</item>
/// </list>
/// </summary>
public sealed class SteerTimeoutPolicyTests
{
    private static SteerTimeoutFacts Facts(
        double secondsWaiting = 200,
        double timeoutSeconds = 120,
        bool hasAnswer = false,
        string? answer = null,
        string? ambiguity = null)
        => new(secondsWaiting, timeoutSeconds, hasAnswer, answer, ambiguity);

    [Fact]
    public void WithinTimeout_KeepsWaiting()
    {
        var d = SteerTimeoutPolicy.Decide(Facts(secondsWaiting: 30, timeoutSeconds: 120));

        Assert.Equal(SteerTimeoutAction.KeepWaiting, d.Action);
        Assert.Equal(SteerTimeoutReasons.WithinTimeout, d.ReasonCode);
    }

    [Fact]
    public void TimedOut_WithConfidentAnswer_AutoAnswers()
    {
        // The 2067 case: "is iframe already implemented?" resolved from the branch
        // state. The answer is fed back so the run continues instead of waiting.
        var d = SteerTimeoutPolicy.Decide(Facts(
            secondsWaiting: 150, timeoutSeconds: 120,
            hasAnswer: true, answer: "Already merged; finalize with [[TASK_DONE]]."));

        Assert.Equal(SteerTimeoutAction.AutoAnswer, d.Action);
        Assert.Equal(SteerTimeoutReasons.AutoAnswered, d.ReasonCode);
        Assert.Equal("Already merged; finalize with [[TASK_DONE]].", d.AnswerText);
    }

    [Fact]
    public void TimedOut_WithAnswerFlagButBlankText_RoutesBlocked()
    {
        // Defensive: a "confident" flag with no text is not a usable answer;
        // fall through to a blocked escalation rather than feeding an empty reply.
        var d = SteerTimeoutPolicy.Decide(Facts(hasAnswer: true, answer: "   "));

        Assert.Equal(SteerTimeoutAction.RouteBlocked, d.Action);
        Assert.Equal(SteerTimeoutReasons.SteerUnanswered, d.ReasonCode);
    }

    [Fact]
    public void TimedOut_NoConfidentAnswer_RoutesBlockedWithReason()
    {
        var d = SteerTimeoutPolicy.Decide(Facts(
            secondsWaiting: 300, timeoutSeconds: 120,
            hasAnswer: false, ambiguity: "the question is a design choice"));

        Assert.Equal(SteerTimeoutAction.RouteBlocked, d.Action);
        Assert.Equal(SteerTimeoutReasons.SteerUnanswered, d.ReasonCode);
        Assert.Contains("design choice", d.Detail);
    }

    [Fact]
    public void TimeoutBoundary_IsInclusive_ActsAtExactlyTimeout()
    {
        // Silence == timeout is past the window (>= budget), so the card is acted
        // on rather than deferred forever at the boundary (acceptance: "nie
        // >T+Toleranz im Wartezustand").
        var atBoundary = SteerTimeoutPolicy.Decide(Facts(secondsWaiting: 120, timeoutSeconds: 120));
        Assert.NotEqual(SteerTimeoutAction.KeepWaiting, atBoundary.Action);

        var justUnder = SteerTimeoutPolicy.Decide(Facts(secondsWaiting: 119.9, timeoutSeconds: 120));
        Assert.Equal(SteerTimeoutAction.KeepWaiting, justUnder.Action);
    }

    [Fact]
    public void ThreeCardEvidence_AllResolve_NoneKeepsWaiting()
    {
        // Belegt 2062/2067/2068 (2026-07-10): three cards hung ~5 hours on steer
        // questions. Reconstructed here as three timed-out facts - two answerable
        // from context (their work was already merged), one not. After Slice B
        // ALL three leave the wait (two auto-answered, one blocked). Not one keeps
        // waiting - the invisible 5-hour hang is gone.
        var fiveHours = TimeSpan.FromHours(5).TotalSeconds;

        var c2067 = SteerTimeoutPolicy.Decide(Facts(
            secondsWaiting: fiveHours, hasAnswer: true, answer: "iframe already merged - finalize."));
        var c2068 = SteerTimeoutPolicy.Decide(Facts(
            secondsWaiting: fiveHours, hasAnswer: true, answer: "dark-mode toggle already merged - finalize."));
        var c2062 = SteerTimeoutPolicy.Decide(Facts(
            secondsWaiting: fiveHours, hasAnswer: false, ambiguity: "the follow-up is a design decision"));

        Assert.Equal(SteerTimeoutAction.AutoAnswer, c2067.Action);
        Assert.Equal(SteerTimeoutAction.AutoAnswer, c2068.Action);
        Assert.Equal(SteerTimeoutAction.RouteBlocked, c2062.Action);

        Assert.DoesNotContain(
            new[] { c2067, c2068, c2062 },
            d => d.Action == SteerTimeoutAction.KeepWaiting);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("1", 5)]
    [InlineData("20", 20)]
    [InlineData("99", 55)]
    public void SweepInterval_DefaultAndClampBoundTimeoutTolerance(string? configured, int expectedSeconds)
    {
        var values = new Dictionary<string, string?>();
        if (configured != null) values["Runner:SteerTimeout:IntervalSeconds"] = configured;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            SteerTimeoutMonitorHostedService.ResolveInterval(config));
    }

    [Fact]
    public void AutoPickedNeedsInput_RemainsUnattendedAfterModeFlipAndWithoutRunPlan()
    {
        var task = new TaskInfo { Id = "AGT-2087" };

        Assert.True(ProjectRunner.ShouldHandleNeedsInputUnattended(
            AgentOutcomeKind.NeedsInput, RunIntent.AutoPickup, "manual", task));
        Assert.False(ProjectRunner.ShouldHandleNeedsInputUnattended(
            AgentOutcomeKind.NeedsInput, RunIntent.ManualStart, "manual", task));
    }

    [Fact]
    public void ConceptNeedsInput_IsAValidSightReviewOutcome_NotAnUnattendedSteer()
    {
        var task = new TaskInfo { Id = "AGT-2358", Mode = TaskModes.Concept };

        Assert.False(ProjectRunner.ShouldHandleNeedsInputUnattended(
            AgentOutcomeKind.NeedsInput, RunIntent.AutoPickup, "auto-continuous", task));
    }
}
