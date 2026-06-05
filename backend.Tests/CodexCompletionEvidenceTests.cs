using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the Codex evidence-based completion evaluator: a silent finish (Codex
/// ended without a terminal sentinel) is accepted when the on-disk evidence
/// shows finished work, driven to a clean finish via a bounded continuation
/// loop when it left open work, and otherwise left to the caller's existing
/// routing. Claude (non-Codex) is always Inconclusive so it stays
/// sentinel-based.
/// </summary>
public class CodexCompletionEvidenceTests
{
    private static CodexCompletionEvidence.Inputs Codex(
        bool hasCommits = true,
        string? resultToken = "Success",
        int openFindings = 0,
        bool timedOut = false,
        int continuationsUsed = 0)
        => new(
            IsCodex: true,
            HasCommits: hasCommits,
            StatusResultToken: resultToken,
            OpenFindingsCount: openFindings,
            TimedOutMidTask: timedOut,
            ContinuationAttemptsUsed: continuationsUsed);

    [Fact]
    public void NonCodex_IsAlwaysInconclusive()
    {
        var inputs = Codex() with { IsCodex = false };
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive,
            CodexCompletionEvidence.Decide(inputs).Action);
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("succeeded")]
    [InlineData("Done")]
    [InlineData("complete")]
    [InlineData("passed")]
    public void CommitsPlusCleanStatus_AcceptAsDone(string token)
    {
        var verdict = CodexCompletionEvidence.Decide(Codex(resultToken: token));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.AcceptAsDone, verdict.Action);
    }

    [Fact]
    public void NoCommits_DoesNotAccept_EvenWithCleanStatus()
    {
        var verdict = CodexCompletionEvidence.Decide(Codex(hasCommits: false));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive, verdict.Action);
    }

    [Theory]
    [InlineData("Partial")]
    [InlineData("Failed")]
    [InlineData("Blocked")]
    [InlineData(null)]
    public void NonSuccessStatus_WithCommits_DoesNotAccept(string? token)
    {
        // No open findings either, so this is neither clean-accept nor open-work:
        // it falls through to the caller's existing routing.
        var verdict = CodexCompletionEvidence.Decide(Codex(resultToken: token));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive, verdict.Action);
    }

    [Fact]
    public void OpenItems_WithBudget_Continues()
    {
        var verdict = CodexCompletionEvidence.Decide(Codex(openFindings: 3));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Continue, verdict.Action);
    }

    [Fact]
    public void TimedOutMidTask_WithBudget_Continues()
    {
        var verdict = CodexCompletionEvidence.Decide(
            Codex(resultToken: null, hasCommits: false, timedOut: true));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Continue, verdict.Action);
    }

    [Fact]
    public void OpenItems_WinOverCleanStatus_Continue()
    {
        // Even a "Success" token cannot accept while open items remain.
        var verdict = CodexCompletionEvidence.Decide(Codex(resultToken: "Success", openFindings: 1));
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Continue, verdict.Action);
    }

    [Fact]
    public void OpenItems_BudgetExhausted_FallsThroughToInconclusive()
    {
        var atBudget = Codex(openFindings: 2,
            continuationsUsed: CodexCompletionEvidence.DefaultContinuationBudget);
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive,
            CodexCompletionEvidence.Decide(atBudget).Action);

        var overBudget = Codex(openFindings: 2,
            continuationsUsed: CodexCompletionEvidence.DefaultContinuationBudget + 5);
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive,
            CodexCompletionEvidence.Decide(overBudget).Action);
    }

    [Fact]
    public void OpenItems_LastBudgetSlot_StillContinues()
    {
        var lastSlot = Codex(openFindings: 1,
            continuationsUsed: CodexCompletionEvidence.DefaultContinuationBudget - 1);
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Continue,
            CodexCompletionEvidence.Decide(lastSlot).Action);
    }

    [Fact]
    public void CustomBudget_IsHonored()
    {
        var inputs = Codex(openFindings: 1, continuationsUsed: 1);
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Inconclusive,
            CodexCompletionEvidence.Decide(inputs, continuationBudget: 1).Action);
        Assert.Equal(CodexCompletionEvidence.CompletionAction.Continue,
            CodexCompletionEvidence.Decide(inputs, continuationBudget: 3).Action);
    }
}
