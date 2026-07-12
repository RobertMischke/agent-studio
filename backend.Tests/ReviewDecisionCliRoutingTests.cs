using AgentStudio.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class ReviewDecisionCliRoutingTests
{
    [Theory]
    [InlineData(null, "codex")]
    [InlineData("", "codex")]
    [InlineData("codex", "codex")]
    [InlineData("/usr/bin/codex", "codex")]
    [InlineData(@"C:\tools\codex.exe", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("gemini", "gemini")]
    public void NormalizeReviewCliType_PreservesConfiguredProvider(string? configured, string expected)
        => Assert.Equal(expected, ReviewDecisionOrchestrator.NormalizeReviewCliType(configured));
}
