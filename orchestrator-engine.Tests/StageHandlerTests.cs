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

    [Fact]
    public async Task Council_reissues_named_critical_findings()
    {
        var decision = await new CouncilLoop().ExecuteAsync(
            Run("""{"reviewFindings":[{"severity":"critical","summary":"unsafe"}]}"""),
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
