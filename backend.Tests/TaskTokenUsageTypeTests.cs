using Xunit;

namespace AgentStudio.Tests;

public class TaskTokenUsageTypeTests
{
    [Theory]
    [InlineData("agent:codex", "codex-turn", TaskTokenUsageType.Coding)]
    [InlineData("support:review", "aspect-review", TaskTokenUsageType.Review)]
    [InlineData("orchestrator:Demo", "final-verdict", TaskTokenUsageType.Gate)]
    [InlineData("support:selector", "prompt-enrichment", TaskTokenUsageType.Enrichment)]
    [InlineData("orchestrator:Demo", "orchestrator-steer", TaskTokenUsageType.Orchestration)]
    [InlineData("support:adhoc", "documentation", TaskTokenUsageType.Supporting)]
    [InlineData(null, null, TaskTokenUsageType.Other)]
    public void Classify_UsesRecordedStepContext(
        string? participantId,
        string? topic,
        string expected)
    {
        Assert.Equal(expected, TaskTokenUsageType.Classify(participantId, topic));
    }
}
