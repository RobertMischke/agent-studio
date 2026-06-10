
using Xunit;

namespace AgentStudio.Tests;

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

    [Theory]
    // ASS-797: a non-actionable "None" follow-up checkbox the agent can never
    // close. Each variant must NOT become a finding, or the task ping-pongs back
    // to 2-ready forever / escalates grundlos.
    [InlineData("- [ ] None. Changes left in working tree per managed-run guidelines.")]
    [InlineData("- None. Changes left in working tree per managed-run guidelines.")]
    [InlineData("- [ ] None.")]
    [InlineData("- [ ] N/A")]
    [InlineData("- [ ] Nothing to do.")]
    [InlineData("- [ ] No follow-ups required.")]
    [InlineData("- [ ] No open items.")]
    public void ExtractFindings_PhantomNoneOpenItem_IsNotReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_PhantomNoneOpenItem_DoesNotTriggerReissueOrEscalate()
    {
        // The whole point of AC3: a phantom "None" item must pass the gate at
        // both ends of the reissue budget, not reissue (budget left) or escalate
        // (budget spent).
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\n- [ ] None. Changes left in working tree per managed-run guidelines.\n";

        var fresh = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);
        var spent = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, fresh.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, spent.Action);
    }

    [Fact]
    public void ExtractFindings_RealItemStartingWithNone_StillReported()
    {
        // Guard the guard: a genuine open item that merely opens with "None"
        // must NOT be swallowed by the phantom-None suppression.
        var status = "## Open Items\n- [ ] None of the routes are wired into the shell yet.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("routes are wired", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    // The reissue loop's own trigger: a commit/push-delegation follow-up the
    // agent can never close because the platform owns the commit boundary
    // (docs/commit-push-doctrine.md). Each variant must NOT become a finding,
    // or the managed run ping-pongs back to 2-ready forever / escalates grundlos.
    [InlineData("- [ ] Working tree changes awaiting managed-run commit/push (platform owns merge).")]
    [InlineData("- [ ] Changes left in the working tree for the platform to commit.")]
    [InlineData("- [ ] Commit/push handled by the managed run.")]
    [InlineData("- [ ] Changes committed by the platform after this run.")]
    [InlineData("- [ ] Awaiting the managed run to push the work.")]
    [InlineData("- [ ] Final commit per managed-run guidelines.")]
    [InlineData("- Changes awaiting commit/push.")]
    public void ExtractFindings_PlatformOwnedCommitItem_IsNotReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_PlatformOwnedCommitItem_DoesNotTriggerReissueOrEscalate()
    {
        // The exact reissue trigger this fix targets: a commit-delegation item
        // must pass the gate at both ends of the reissue budget.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\n- [ ] Working tree changes awaiting managed-run commit/push (platform owns merge).\n";

        var fresh = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);
        var spent = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, fresh.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, spent.Action);
    }

    [Theory]
    // Guard the guard: genuine work that happens to mention commit/push/merge
    // must still be reported. The delegation must be explicit to suppress.
    [InlineData("- [ ] Push the migration to the shared config repo.")]
    [InlineData("- [ ] Commit the generated client SDK before release.")]
    [InlineData("- [ ] Merge the feature branch into develop once review passes.")]
    public void ExtractFindings_RealCommitWork_StillReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
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
    public void ExtractFindings_GreppedSourceLineWithKeyword_InLogTail_IsNotReported()
    {
        // ASS-794 regression: the previous run grepped its own source and echoed
        // a line whose only "incomplete" token is a PARAMETER named `pending`.
        // The gate must not read an identifier in printed code as unfinished work
        // and reissue the task forever.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n";
        var log = string.Join('\n',
            "[17:15:20.243] [stdout] running grep",
            "[17:15:20.243] [stdout]   814:                HandleAcceptAsDone(workspace, entry, pending, prompt, response, verdict);",
            "[17:15:21.000] [stdout] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_RealPendingProse_StillReported()
    {
        // Guard the guard: a genuine status sentence using "pending" as a status
        // word (no line-number/code-echo shape) must still be caught.
        var status = string.Join('\n',
            "## Notes",
            "The route wiring is still pending.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_GreppedBuildErrorString_InLogTail_IsNotReported()
    {
        // A grepped source line that merely contains the literal "build failed"
        // inside code (e.g. an error-message string) is an echo, not the run's
        // own close-out, so it must not trip the build-error vocabulary.
        var status = "## Summary\nDone.\n\nResult: Success\n";
        var log = string.Join('\n',
            "[12:00] grep build",
            "[12:00]   42:    throw new Exception(\"build failed unexpectedly\");",
            "[12:01] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
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
