using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

public class RunOutcomeContractTests
{
    public static IEnumerable<object[]> TerminalCases()
    {
        yield return [AgentOutcomeKind.Done, "success", "Success"];
        yield return [AgentOutcomeKind.NoOp, "noop", "NoOp"];
        yield return [AgentOutcomeKind.Blocked, "blocked", "Blocked"];
        yield return [AgentOutcomeKind.NeedsInput, "needs-input", "NeedsInput"];
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void SentinelOutcome_DrivesLaneProtocolAndToastFromOneClassification(
        AgentOutcomeKind kind,
        string expectedKind,
        string expectedProtocolResult)
    {
        var outcome = new AgentOutcome(
            Kind: kind,
            Summary: $"sentinel {kind}",
            MatchedSentinel: true,
            SentinelKeyword: kind.ToString().ToUpperInvariant(),
            Reason: null,
            AgentTextChars: 20,
            OutputLineCount: 2,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(RunStatuses.Failed, outcome);

        Assert.Equal(expectedKind, terminal.Kind);
        Assert.Equal(expectedProtocolResult, terminal.ProtocolResult);
        Assert.True(RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.False(terminal.ShouldShowFailureToast);

        var protocol = OrchestratorApi.Services.SummaryGenerationService.ApplyOutcomeResultLine(
            "# Status\n\n- Result: Failed\n- Duration: 4 sec\n",
            terminal.ProtocolResult);
        Assert.Contains($"- Result: {expectedProtocolResult}", protocol);
    }

    [Fact]
    public void FailedRunWithoutTerminalSignal_StaysFailedEverywhere()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "crash",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 0,
            OutputLineCount: 1,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(RunStatuses.Failed, outcome);

        Assert.Equal("failed", terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }

    // Regression for the bug "Codex exit=-1 trotz erfolgreichem Commit landet in
    // 4-auto-review verdict=reissue + ATP-manual": a run that committed real work
    // (commitCount > 0) but then exited -1 because its downstream test runs were
    // killed must NOT be treated as a hard failure. It is an honest "partial":
    // route it to review (4-auto-review) with a Partial verdict and no crash
    // toast, so auto-continuous mode is preserved and the successful commit is
    // not silently discarded by a reissue loop.
    //
    // AC#1: exit=-1 + commitCount > 0 is not classified as hard-fail.
    // AC#3: expect the card in 4-auto-review with a partial (not reissue) verdict.
    [Fact]
    public void FailedRunThatCommitted_RoutesToReviewAsCommittedPartial()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "tests killed after commit",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 240,
            OutputLineCount: 30,
            DurationSeconds: 42.0);

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 2);

        Assert.Equal("committed-partial", terminal.Kind);
        Assert.Equal("Partial", terminal.ProtocolResult);
        Assert.True(terminal.ShouldMoveToReview);
        Assert.True(RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.False(terminal.ShouldShowFailureToast);
    }

    // The commit-aware branch must not soften an honest crash: a failed run that
    // committed nothing (commitCount == 0) stays a hard failure everywhere. This
    // pins the boundary so the fix above cannot drift into swallowing real crashes.
    [Fact]
    public void FailedRunWithZeroCommits_StaysHardFailure()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "crash",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 0,
            OutputLineCount: 1,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 0);

        Assert.Equal("failed", terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }

    // The bug this guards: five identical Codex runs (all exitCode=-1, all
    // [[TASK_NOOP]]) produced three different status.md values, four different
    // target lanes, and an inconsistent failure toast, because lane routing,
    // status.md, and the toast each classified the run independently. The
    // existing cases above pin the synthetic-AgentOutcome layer; this drives
    // the real rendered cli-output.log path (TryClassifyRenderedLog), which is
    // where the exitCode=-1 exit line actually enters the classifier. A hard
    // sentinel must win over the Windows kill artifact (exitCode=-1) so every
    // consumer derives the same answer from one classification.
    public static IEnumerable<object[]> CodexExitMinusOneCases()
    {
        // sentinel, expected wire kind, expected protocol result, moves to review, failure toast
        yield return ["[[TASK_NOOP]]", "noop", "NoOp", true, false];
        yield return ["[[TASK_DONE]]", "success", "Success", true, false];
        yield return ["[[TASK_BLOCKED:cannot reach API]]", "blocked", "Blocked", true, false];
        yield return ["[[TASK_NEEDS_INPUT:which file?]]", "needs-input", "NeedsInput", true, false];
    }

    [Theory]
    [MemberData(nameof(CodexExitMinusOneCases))]
    public void CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers(
        string sentinel,
        string expectedKind,
        string expectedProtocolResult,
        bool expectedMovesToReview,
        bool expectedFailureToast)
    {
        // A rendered cli-output.log tail exactly as it lands on disk: agent
        // text on stdout, then the synthetic taskboard exit line reporting the
        // Windows kill artifact (status=failed, exitCode=-1).
        var renderedLog = string.Join("\n",
            "[2026-06-01T18:00:00.000Z] [stdout] Looked at the repository state for this task.",
            $"[2026-06-01T18:00:01.000Z] [stdout] {sentinel}",
            "[2026-06-01T18:00:05.000Z] [system] [taskboard] codex CLI exited: status=failed, exitCode=-1, duration=4.2s");

        var classified = TerminalRunOutcomeClassifier.TryClassifyRenderedLog(renderedLog);
        Assert.NotNull(classified);
        var terminal = classified!.Value.Outcome;

        // The sentinel was recognised despite the failed/-1 process exit.
        Assert.True(classified.Value.AgentOutcome.MatchedSentinel);

        // One classification, read three ways - the divergence killer.
        Assert.Equal(expectedKind, terminal.Kind);                                  // toast wire value (execution.runOutcome)
        Assert.Equal(expectedProtocolResult, terminal.ProtocolResult);              // status.md "- Result:" line
        Assert.Equal(expectedMovesToReview, terminal.ShouldMoveToReview);           // lane routing
        Assert.Equal(expectedMovesToReview, RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.Equal(expectedFailureToast, terminal.ShouldShowFailureToast);        // failure toast/modal gating

        // status.md is forced to the same outcome, never left at the Haiku guess.
        var protocol = OrchestratorApi.Services.SummaryGenerationService.ApplyOutcomeResultLine(
            "# Status\n\n- Result: Failed\n- Duration: 4 sec\n",
            terminal.ProtocolResult);
        Assert.Contains($"- Result: {expectedProtocolResult}", protocol);
    }

    [Fact]
    public void CodexExitMinusOne_NoOp_DoesNotDivergeFromSentinelOnlyClassification()
    {
        // Pin the (exitCode, reason) -> status step the rendered-log path
        // depends on: Windows hands back -1 on a natural process death, so the
        // raw status is "failed", yet the [[TASK_NOOP]] sentinel still wins.
        var status = RunStatusClassifier.Classify(-1, RunStopReason.None);
        Assert.Equal(RunStatuses.Failed, status);

        var lines = new List<CliOutputLine>
        {
            new() { Stream = "stdout", Text = "No changes were necessary." },
            new() { Stream = "stdout", Text = "[[TASK_NOOP]]" }
        };

        var terminal = TerminalRunOutcomeClassifier.Classify(status, lines, durationSeconds: 4.2);

        Assert.Equal("noop", terminal.Kind);
        Assert.Equal("NoOp", terminal.ProtocolResult);
        Assert.True(terminal.ShouldMoveToReview);
        Assert.False(terminal.ShouldShowFailureToast);
    }

    [Fact]
    public void SummaryResultLine_IsForcedToCanonicalOutcome()
    {
        var summary = """
            # Status

            - Result: Failed
            - Duration: 4 sec

            ## What Was Done
            - Nothing.

            ## Open Items
            - None.
            """;

        var updated = OrchestratorApi.Services.SummaryGenerationService.ApplyOutcomeResultLine(summary, "NoOp");

        Assert.Contains("- Result: NoOp", updated);
        Assert.DoesNotContain("- Result: Failed", updated);
    }
}
