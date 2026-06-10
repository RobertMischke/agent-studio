
using Xunit;

namespace AgentStudio.Tests;

public class SolutionQualityGateTests
{
    [Fact]
    public void Evaluate_RequirementFitGoalMissConcern_Reissues()
    {
        var report = ReportWith(
            ("requirement-fit", AspectStatus.Concerns, "Core task goal not met; only comments changed."),
            ("tests-and-evidence", AspectStatus.Pass, "ok"));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Reissue, decision.Action);
        Assert.Single(decision.Findings);
        Assert.Contains("requirement-fit", decision.Findings[0]);
    }

    [Fact]
    public void Evaluate_RedundantWorkConcern_Reissues()
    {
        var report = ReportWith(
            ("code-quality", AspectStatus.Concerns, "The diff reimplements existing behavior and the new path is not wired."));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Reissue, decision.Action);
        Assert.Contains("reimplements", decision.Findings[0]);
    }

    [Fact]
    public void Evaluate_OrdinaryDuplicationConcern_Passes()
    {
        var report = ReportWith(
            ("code-quality", AspectStatus.Concerns, "Helper logic is duplicated and could be tidied later."));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Pass, decision.Action);
        Assert.False(decision.IsBlocking);
    }

    [Fact]
    public void Evaluate_MinorRedundantHelperConcern_Passes()
    {
        var report = ReportWith(
            ("code-quality", AspectStatus.Concerns, "A redundant local helper could be folded into the shared formatter later."));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Pass, decision.Action);
    }

    [Fact]
    public void Evaluate_TestsConcern_DoesNotOwnEvidenceGateWork()
    {
        var report = ReportWith(
            ("tests-and-evidence", AspectStatus.Concerns, "Build failed with error CS0246."));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Pass, decision.Action);
    }

    [Fact]
    public void Evaluate_BudgetExhausted_Escalates()
    {
        var report = ReportWith(
            ("requirement-fit", AspectStatus.Concerns, "Task goal not met; the required API endpoint is missing."));

        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 2, maxReissues: 2);

        Assert.Equal(SolutionQualityGate.SolutionQualityGateAction.Escalate, decision.Action);
    }

    [Fact]
    public void BuildFollowUp_IncludesAntiRedundantWorkInstruction()
    {
        var report = ReportWith(
            ("requirement-fit", AspectStatus.Concerns, "Work is redundant; this migration was already implemented."));
        var decision = SolutionQualityGate.Evaluate(report, priorReissues: 0, maxReissues: 2);

        var followUp = SolutionQualityGate.BuildFollowUp(decision);

        Assert.Contains("[[TASK_DONE]]", followUp);
        Assert.Contains("Do not redo already-complete work", followUp);
        Assert.Contains("[[TASK_BLOCKED", followUp);
    }

    private static AspectRunReport ReportWith(params (string Aspect, AspectStatus Status, string Summary)[] verdicts)
    {
        var list = verdicts
            .Select(v => new AspectVerdict(
                v.Aspect, v.Status, v.Summary, Body: "",
                ConcernTagId: v.Status == AspectStatus.Pass ? null : "quality:concerns"))
            .ToList();
        return AspectRunReport.From(list);
    }
}
