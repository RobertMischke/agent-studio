using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskTokenUsageClassifierTests
{
    [Theory]
    [InlineData("agent:codex", "codex-turn", TaskTokenUsageTypes.CodingRun)]
    [InlineData("support:code-quality", "code-quality", TaskTokenUsageTypes.ReviewRun)]
    [InlineData("orchestrator:PROJ", "orchestrator-review", TaskTokenUsageTypes.ReviewRun)]
    [InlineData("support:prompt", "prompt-enrichment", TaskTokenUsageTypes.Enrichment)]
    [InlineData("support:verify", "build-test-gate", TaskTokenUsageTypes.Gate)]
    [InlineData("orchestrator:PROJ", "orchestrator-decision", TaskTokenUsageTypes.Orchestration)]
    [InlineData(null, null, TaskTokenUsageTypes.Other)]
    public void Classify_UsesParticipantAndStepContext(
        string? participantId,
        string? topic,
        string expected)
    {
        Assert.Equal(expected, TaskTokenUsageClassifier.Classify(participantId, topic));
    }
}
