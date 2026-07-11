using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Fixtures for the three 2026-07-11 false escalations. The policy is pure so
/// these cases do not depend on a task folder, git repository, or orchestrator.
/// </summary>
public class CompletionEvidencePolicyTests
{
    [Fact]
    public void Mkt1_VerificationClosure_WithResultsAndNoNewCommit_IsComplete()
    {
        var decision = Gate(
            status: "Result: Success\n\n## Open Items\nNone\n\n## Notes\nIndependent verification confirmed 0 dead links.",
            results: true,
            exitCode: -1);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.Contains("ResultsArtifact", decision.Reason);
        Assert.Contains("DocumentedVerification", decision.Reason);
    }

    [Fact]
    public void Ass1761_DocumentationUploadedThroughTaskApi_WithNoNewCommit_IsComplete()
    {
        var decision = Gate(
            status: "Result: Success\n\nUploaded the two wiki pages via the Task API.\n\n## Open Items\nNone\n");

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.Contains("ApiDelivery", decision.Reason);
    }

    [Fact]
    public void Agt2077_RestoreAndEightPassingTests_SupersedeHistoricalFailure()
    {
        var decision = Gate(
            status: "Result: Success\nProblem: prior build failure.\nSolution: build now passes with 0 errors; 8/8 tests passed.\n\n## Open Items\nNone\n");

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.Contains("DocumentedVerification", decision.Reason);
    }

    [Fact]
    public void CurrentBuildFailureWithoutSuccessfulVerification_RemainsIncomplete()
    {
        var decision = Decide(
            status: "Result: Success\nFinal build failed with error CS0246.\n\n## Open Items\nNone\n",
            buildFailure: true,
            results: true);

        Assert.False(decision.AcceptAsCompleted);
    }

    private static CompletionEvidencePolicy.Decision Decide(
        string status,
        bool apiDelivery = false,
        bool results = false,
        bool buildFailure = false)
        => CompletionEvidencePolicy.Decide(new CompletionEvidencePolicy.Inputs(
            HasTaskDoneSentinel: true,
            ExitCode: 0,
            RunStatusCompleted: true,
            StatusResultToken: CompletionGate.ExtractResultToken(status),
            HasOpenItems: false,
            HasBuildFailureInStatus: buildFailure,
            HasApiDelivery: apiDelivery,
            HasResultsArtifacts: results,
            HasDocumentedVerification: CompletionEvidencePolicy.DetectDocumentedVerification(status)));

    private static CompletionGate.Decision Gate(string status, bool results = false, int exitCode = 0)
        => CompletionGate.Evaluate(
            status,
            $"[[TASK_DONE]]\n[12:00:01.000] [system] [taskboard] codex CLI exited: status=completed, exitCode={exitCode}, duration=1.0s",
            priorReissues: 2,
            maxReissues: 2,
            hasResultsArtifacts: results);
}
