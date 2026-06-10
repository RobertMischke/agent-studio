
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure check + action rules for the per-project meta-cycle.
/// No DI, no I/O, no hosted service: pass an inspection in, assert the
/// verdict and the chosen action come back as expected. The rules govern
/// every cycle the orchestrator runs, so a regression here ripples into
/// every project that has the meta-cycle enabled.
/// </summary>
public class MetaCycleRulesTests
{
    private static MetaCycleInspection Healthy() => new(
        CommitLogDiff: new MetaCycleCommitLogDiff(3, "abc", "def"),
        LastCrashMarker: new MetaCycleCrashMarker(false, null, null),
        SupervisorAdvisories: new MetaCycleAdvisorySummary(0, Array.Empty<string>()),
        StuckInProgress: new MetaCycleStuckInProgress(0, Array.Empty<string>()),
        ExpectedArtefacts: new MetaCycleExpectedArtefacts(0, Array.Empty<string>()),
        RunnerModeDrift: new MetaCycleRunnerModeDrift(false, "auto-continuous", "auto-continuous"));

    private static IReadOnlyList<MetaCycleJobObservation> TwoJobs() => new[]
    {
        new MetaCycleJobObservation("job-a", "Job A", 2, true),
        new MetaCycleJobObservation("job-b", "Job B", 1, true),
    };

    [Fact]
    public void HealthyBatch_DerivesNoFindings_ResumeAction()
    {
        var inspection = Healthy();
        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-1",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults(),
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Empty(report.Findings);
        Assert.Equal(MetaCycleVerdict.Healthy, report.Verdict);
        Assert.Equal(MetaCycleActionKind.Resume, report.Action.Kind);
    }

    [Fact]
    public void HealthyBatch_RunUpdateStableSet_PicksUpdateStableThenResume()
    {
        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-2",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults() with { RunUpdateStableOnHealthy = true },
            jobs: TwoJobs(),
            inspection: Healthy(),
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Equal(MetaCycleVerdict.Healthy, report.Verdict);
        Assert.Equal(MetaCycleActionKind.UpdateStableThenResume, report.Action.Kind);
    }

    [Fact]
    public void CrashMarker_TriggersFix_QueuesOrphanChangesTemplate()
    {
        var inspection = Healthy() with
        {
            LastCrashMarker = new MetaCycleCrashMarker(true, DateTime.UtcNow, "rescued orphan changes"),
        };

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-3",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults(),
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Equal(MetaCycleVerdict.FixTriggering, report.Verdict);
        Assert.Equal(MetaCycleActionKind.QueueFix, report.Action.Kind);
        Assert.Contains("last-crash-marker", report.Action.Reason);
        Assert.Equal("1-preparation", report.Action.FollowUpState);
    }

    [Fact]
    public void RateLimitedFix_EscalatesToUserInsteadOfQueueing()
    {
        var inspection = Healthy() with
        {
            LastCrashMarker = new MetaCycleCrashMarker(true, DateTime.UtcNow, "another crash"),
        };

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-4",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults() with { MaxFixesPerHour = 2 },
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 2);

        Assert.Equal(MetaCycleVerdict.FixTriggering, report.Verdict);
        Assert.Equal(MetaCycleActionKind.EscalateToUser, report.Action.Kind);
        Assert.Equal("auto-fix-rate-limit", report.Action.Reason);
    }

    [Fact]
    public void EscalateOnlyTopic_AlwaysEscalates_NeverQueuesFix()
    {
        var inspection = Healthy() with
        {
            // Use the supervisor-advisory mechanism to surface a topic; then add
            // an extra-advisory hook so the topic appears as an escalate-only
            // finding via the topic name itself.
            SupervisorAdvisories = new MetaCycleAdvisorySummary(1, new[] { "supervisor-advisories" }),
        };

        // Force the escalate-only path by sneaking in a finding with a topic
        // listed in EscalateOnlyTopics. We do this through the public
        // DeriveVerdict so we exercise the rule directly.
        var findings = new[]
        {
            new MetaCycleFinding("prompt-regression", SupervisorSeverity.High, "Prompt template regressed."),
        };

        var verdict = MetaCycleRules.DeriveVerdict(findings);
        Assert.Equal(MetaCycleVerdict.EscalationOnly, verdict);

        var action = MetaCycleRules.DeriveAction(verdict, findings, runUpdateStable: false, autoFixesInTrailingHour: 0, maxFixesPerHour: 5);
        Assert.Equal(MetaCycleActionKind.EscalateToUser, action.Kind);
        Assert.Contains("prompt-regression", action.Reason);
        Assert.Equal("1-preparation", action.FollowUpState);
    }

    [Fact]
    public void StuckInProgress_TriggersFix_StuckProgressTemplate()
    {
        var inspection = Healthy() with
        {
            StuckInProgress = new MetaCycleStuckInProgress(1, new[] { "wedged-job" }),
        };

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-5",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults(),
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Equal(MetaCycleVerdict.FixTriggering, report.Verdict);
        Assert.Equal(MetaCycleActionKind.QueueFix, report.Action.Kind);
        Assert.Contains("stuck-in-progress", report.Action.Reason);
    }

    [Fact]
    public void AutoCommitOff_ZeroCommitsIsHealthy()
    {
        var inspection = Healthy() with
        {
            CommitLogDiff = new MetaCycleCommitLogDiff(0, "abc", "abc"),
        };

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-6",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults(),
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: false,
            autoFixesInTrailingHour: 0);

        Assert.Empty(report.Findings);
        Assert.Equal(MetaCycleVerdict.Healthy, report.Verdict);
        Assert.Equal(MetaCycleActionKind.Resume, report.Action.Kind);
    }

    [Fact]
    public void AutoCommitOn_ZeroCommitsTriggersFix()
    {
        var inspection = Healthy() with
        {
            CommitLogDiff = new MetaCycleCommitLogDiff(0, "abc", "abc"),
        };

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-7",
            project: "p",
            startedAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            config: MetaCycleConfig.Defaults(),
            jobs: TwoJobs(),
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Single(report.Findings);
        Assert.Equal(MetaCycleVerdict.FixTriggering, report.Verdict);
        Assert.Equal(MetaCycleActionKind.QueueFix, report.Action.Kind);
        Assert.Contains("commit-log-diff", report.Action.Reason);
    }

    [Fact]
    public void Aborted_VerdictPicksNoOpAction()
    {
        var action = MetaCycleRules.DeriveAction(
            MetaCycleVerdict.Aborted,
            findings: Array.Empty<MetaCycleFinding>(),
            runUpdateStable: true,
            autoFixesInTrailingHour: 0,
            maxFixesPerHour: 5);

        Assert.Equal(MetaCycleActionKind.NoOp, action.Kind);
        Assert.Equal("aborted", action.Reason);
    }

    [Fact]
    public void HighSeverityWithoutTemplate_FallsBackToEscalate()
    {
        var findings = new[]
        {
            new MetaCycleFinding("unmapped-topic", SupervisorSeverity.High, "something we have no template for"),
        };
        var verdict = MetaCycleRules.DeriveVerdict(findings);
        var action = MetaCycleRules.DeriveAction(verdict, findings, runUpdateStable: false, autoFixesInTrailingHour: 0, maxFixesPerHour: 5);

        Assert.Equal(MetaCycleVerdict.FixTriggering, verdict);
        Assert.Equal(MetaCycleActionKind.EscalateToUser, action.Kind);
        Assert.Equal("no-template-for-finding", action.Reason);
    }
}
