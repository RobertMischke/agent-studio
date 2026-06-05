using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the deterministic post-DONE completion gate: the scan that reads a
/// run's own close-out (status Open Items / Notes / Result line plus the log
/// tail) for unfinished-work evidence before auto-review may accept the task.
/// Covers the expanded build/compile/test-failure vocabulary and the
/// "claims success but build failed" contradiction rule that closed the
/// ASS-764 / ASS-766 silent-completion gap.
/// </summary>
public class CompletionGateTests
{
    private const int MaxReissues = 3;

    [Fact]
    public void ExtractFindings_CleanCloseOut_NoFindings()
    {
        var status = string.Join('\n',
            "## Summary",
            "Implemented the feature and verified the build.",
            "",
            "Result: Success",
            "",
            "## Open Items",
            "None");

        var findings = CompletionGate.ExtractFindings(status, recentLog: "[12:00] build succeeded");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_OpenItemsCheckbox_IsReported()
    {
        var status = string.Join('\n',
            "## Open Items",
            "- [ ] Wire the new route into the shell",
            "- [x] Already done thing");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("Wire the new route", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_PartialResult_IsReported()
    {
        var status = "Result: Partial";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("Partial", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("The build failed during compilation.")]
    [InlineData("Note: build is broken on main.")]
    [InlineData("The component does not compile yet.")]
    [InlineData("It won't compile because of a missing import.")]
    [InlineData("Saw compile errors in the runner.")]
    [InlineData("error TS2304: cannot find name 'Foo'.")]
    [InlineData("error CS0103: the name does not exist.")]
    [InlineData("error NG8001: unknown element.")]
    [InlineData("There are typescript errors remaining.")]
    [InlineData("npm ERR! build step exited with 1.")]
    [InlineData("Application bundle generation failed.")]
    [InlineData("Several tests failed in the suite.")]
    public void ExtractFindings_BuildErrorVocabulary_InNotes_IsReported(string noteLine)
    {
        var status = string.Join('\n', "## Notes", noteLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_BuildErrorInLogTail_IsReported_WhenStatusOmitsIt()
    {
        var status = "## Summary\nLooks good.";
        var log = string.Join('\n',
            "[12:00] running ng build",
            "[12:01] error TS2552: cannot find name 'foo'",
            "[12:02] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_SuccessResult_WithBuildError_RaisesContradiction()
    {
        var status = string.Join('\n',
            "Result: Success",
            "## Notes",
            "Final build failed with error CS0246.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f =>
            f.Contains("Status result is Success", System.StringComparison.OrdinalIgnoreCase) &&
            f.Contains("build/test failure evidence", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_SuccessResult_NoBuildError_NoContradiction()
    {
        var status = string.Join('\n',
            "Result: Success",
            "## Notes",
            "Everything verified, build is green.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.DoesNotContain(findings, f =>
            f.Contains("build/test failure evidence", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_NoFindings_Passes()
    {
        var decision = CompletionGate.Evaluate("Result: Success", recentLog: null, priorReissues: 0, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.False(decision.IsIncomplete);
    }

    [Fact]
    public void Evaluate_FindingsWithBudgetLeft_Reissues()
    {
        var status = "## Open Items\n- [ ] finish the migration";

        var decision = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Reissue, decision.Action);
        Assert.True(decision.IsIncomplete);
        Assert.NotEmpty(decision.Findings);
    }

    [Fact]
    public void Evaluate_FindingsWithBudgetExhausted_Escalates()
    {
        var status = "## Open Items\n- [ ] finish the migration";

        var decision = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Escalate, decision.Action);
        Assert.True(decision.IsIncomplete);
    }

    [Fact]
    public void BuildFollowUp_RendersFindingsAsCheckboxes()
    {
        var followUp = CompletionGate.BuildFollowUp(new[] { "wire the route", "fix the build" });

        Assert.Contains("- [ ] wire the route", followUp);
        Assert.Contains("- [ ] fix the build", followUp);
        Assert.Contains("[[TASK_DONE]]", followUp);
        Assert.Contains("[[TASK_BLOCKED", followUp);
    }

    /// <summary>
    /// ASS-734: the completion-gate follow-up must also lead with the diff-only
    /// steering rule and list the already-made commits when supplied, so the
    /// reissue resolves the open findings on top of existing work.
    /// </summary>
    [Fact]
    public void BuildFollowUp_LeadsWithDiffOnlyRule_AndListsPriorCommits()
    {
        var followUp = CompletionGate.BuildFollowUp(
            new[] { "wire the route" },
            priorCommits: new[] { "a1b2c3 feat: scaffold route" });

        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, followUp);
        Assert.Contains("Commits already made for this task", followUp);
        Assert.Contains("a1b2c3 feat: scaffold route", followUp);
        Assert.Contains("- [ ] wire the route", followUp);
    }
}
