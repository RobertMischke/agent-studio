using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ASS-734 traceability half: every steering step the orchestrator drives must
/// be reconstructable - the exact prompt the agent received plus the context it
/// was given (resume vs fresh, prior attempt count, reason, prior commits).
/// These lock the rendered steering-history surface that
/// <see cref="ReviewDecisionOrchestrator"/> persists per step under
/// <c>orchestrator-follow-up-history/</c> (never overwritten).
/// </summary>
public class OrchestratorSteeringTraceabilityTests
{
    [Fact]
    public void RenderSteeringHistory_IncludesContextAndVerbatimPrompt()
    {
        var context = new ReviewDecisionOrchestrator.SteeringContext(
            Cause: "no-completion-signal",
            Verdict: "reissue",
            PriorReissues: 1,
            Reason: "run finished without a terminal sentinel",
            ResumeSessionId: "sess-123",
            PriorCommits: new[] { "a1b2c3 feat: lane move" });

        var rendered = ReviewDecisionOrchestrator.RenderSteeringHistory(
            context, "STEER THE DIFF...\n\nDo only the open work.");

        Assert.Contains("## Context", rendered);
        Assert.Contains("cause: no-completion-signal", rendered);
        Assert.Contains("verdict: reissue", rendered);
        Assert.Contains("priorReissues: 1", rendered);
        Assert.Contains("mode: resume", rendered);
        Assert.Contains("resumeSessionId: sess-123", rendered);
        Assert.Contains("reason: run finished without a terminal sentinel", rendered);
        Assert.Contains("a1b2c3 feat: lane move", rendered);
        // The verbatim prompt is preserved so the operator sees exactly what the
        // agent was told.
        Assert.Contains("## Steering prompt (verbatim)", rendered);
        Assert.Contains("Do only the open work.", rendered);
    }

    [Fact]
    public void RenderSteeringHistory_MarksFreshRun_WhenNoResumeSession()
    {
        var context = new ReviewDecisionOrchestrator.SteeringContext(
            Cause: "noop-recovery",
            Verdict: "reissue",
            PriorReissues: 0);

        var rendered = ReviewDecisionOrchestrator.RenderSteeringHistory(context, "prompt body");

        Assert.Contains("mode: fresh-run", rendered);
        Assert.DoesNotContain("resumeSessionId", rendered);
        Assert.Contains("prompt body", rendered);
    }
}
