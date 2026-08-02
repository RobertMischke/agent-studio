using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorEngine.Tests;

public sealed class StageHandlerTests
{
    [Theory]
    [InlineData("""{"agentOutcome":"done"}""", OrchestrationAction.Continue)]
    [InlineData("""{"agentOutcome":"needs-input"}""", OrchestrationAction.Reissue)]
    [InlineData("""{"agentOutcome":"blocked"}""", OrchestrationAction.Escalate)]
    public async Task Review_decision_loop_maps_terminal_facts(
        string payload,
        OrchestrationAction expected)
    {
        var decision = await new ReviewDecisionOrchestratorLoop()
            .ExecuteAsync(Run(payload), default);
        Assert.Equal(expected, decision.Action);
    }

    [Theory]
    [InlineData("Pass", OrchestrationAction.Continue)]
    [InlineData("ProductFailure", OrchestrationAction.Reissue)]
    [InlineData("ReviewInfra", OrchestrationAction.Escalate)]
    public async Task Review_decision_loop_maps_normalized_remote_review_verdicts(
        string outcome,
        OrchestrationAction expected)
    {
        var decision = await new ReviewDecisionOrchestratorLoop().ExecuteAsync(
            Run($$"""{"reviewOutcome":"{{outcome}}"}"""),
            default);

        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public async Task Council_reissues_named_critical_findings()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"reviewFindings":[{"severity":"critical","summary":"unsafe"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Fact]
    public async Task Council_reissues_normalized_remote_review_concerns()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"verdicts":[{"aspect":"requirements","status":"concerns"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    [Fact]
    public async Task Gate_dispatch_reissues_a_failed_gate()
    {
        var decision = await new GateDispatchLoop().ExecuteAsync(
            Run("""{"gates":[{"status":"failed"}]}"""),
            default);
        Assert.Equal(OrchestrationAction.Reissue, decision.Action);
    }

    private static OrchestrationRunDto Run(string payload)
        => new(
            "run-1",
            "project-1",
            "task-1",
            1,
            "leased",
            OrchestrationStage.ReviewDecision,
            payload,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null,
            []);
}
