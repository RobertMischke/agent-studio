using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the advisory-to-action mapping. The auto-intervention policy is the
/// riskiest part of the supervisor (it actually calls emergency primitives),
/// so the rules are deliberately small and explicit; this test pins the
/// table including the feedback-loop guard.
/// </summary>
public class AutoInterventionMappingTests
{
    private static SupervisorAdvisory Adv(
        string topic,
        SupervisorSeverity severity = SupervisorSeverity.High,
        SupervisorSource source = SupervisorSource.HardCheck,
        string? jobId = "j") =>
        new(
            CreatedAt: new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc),
            Project: "p",
            Severity: severity,
            Source: source,
            Topic: topic,
            Message: "msg",
            JobId: jobId);

    [Theory]
    [InlineData("quota-critical", SupervisorInterventionKind.PausePickup)]
    [InlineData("error-burst", SupervisorInterventionKind.PausePickup)]
    [InlineData("no-progress", SupervisorInterventionKind.CancelRun)]
    [InlineData("tool-call-repeat", SupervisorInterventionKind.CancelRun)]
    public void HighSeverityKnownTopic_MapsToExpectedKind(string topic, SupervisorInterventionKind expected)
    {
        var a = Adv(topic);
        var action = AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.High);
        Assert.NotNull(action);
        Assert.Equal(expected, action!.Kind);
    }

    [Fact]
    public void UnknownTopic_NoAction()
    {
        Assert.Null(AutoInterventionMapping.MapAdvisory(Adv("mystery"), SupervisorSeverity.High));
    }

    [Fact]
    public void BelowThreshold_NoAction()
    {
        var a = Adv("no-progress", severity: SupervisorSeverity.Info);
        Assert.Null(AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.High));
    }

    [Fact]
    public void FeedbackLoopGuard_AutoIntervention_NeverActsOnSelf()
    {
        var a = Adv("no-progress", source: SupervisorSource.AutoIntervention);
        Assert.Null(AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.High));
    }

    [Fact]
    public void FeedbackLoopGuard_User_IsNoLongerActedUpon()
    {
        var a = Adv("no-progress", source: SupervisorSource.User);
        Assert.Null(AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.High));
    }

    [Fact]
    public void AdvisoryAtThreshold_IsActedUpon()
    {
        var a = Adv("no-progress", severity: SupervisorSeverity.Warn);
        var action = AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.Warn);
        Assert.NotNull(action);
    }

    [Fact]
    public void ReasonStringContainsAdvisoryMessage()
    {
        var a = Adv("quota-critical");
        var action = AutoInterventionMapping.MapAdvisory(a, SupervisorSeverity.High);
        Assert.NotNull(action);
        Assert.Contains("msg", action!.Reason);
        Assert.Contains("auto:", action.Reason);
    }
}
