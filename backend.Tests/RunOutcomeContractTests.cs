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
