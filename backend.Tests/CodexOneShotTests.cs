using Xunit;

namespace AgentStudio.Tests;

public sealed class CodexOneShotTests
{
    [Fact]
    public void ParseOutput_ExtractsFinalReplyAndSparkUsage()
    {
        const string stdout = """
            {"type":"thread.started","thread_id":"019-test"}
            {"type":"item.completed","item":{"type":"reasoning","text":"hidden"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"[[ASPECT_VERDICT: status=pass; summary=Spark handled the summary.]]"}}
            {"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":800,"output_tokens":42,"reasoning_output_tokens":9}}
            """;
        var started = DateTime.UtcNow;

        var result = CodexOneShot.ParseOutput(
            0, stdout, "", "gpt-5.3-codex-spark", started, started.AddMilliseconds(250),
            new CodexUsageParser(), new CliModelRegistry());

        Assert.True(result.Ok);
        Assert.Contains("Spark handled the summary", result.ParsedText);
        Assert.Equal("gpt-5.3-codex-spark", result.Usage!.Model);
        Assert.Equal(1200, result.Usage.InputTokens);
        Assert.Equal(800, result.Usage.CacheReadTokens);
        Assert.Equal(42, result.Usage.OutputTokens);
        Assert.Equal(9, result.RichUsage!.ReasoningOutput);
    }

    [Fact]
    public void ParseOutput_TurnFailureFailsEvenWhenProcessExitIsZero()
    {
        const string stdout = """
            {"type":"turn.failed","error":{"message":"model is unavailable"}}
            """;
        var now = DateTime.UtcNow;

        var result = CodexOneShot.ParseOutput(
            0, stdout, "", "gpt-5.3-codex-spark", now, now,
            new CodexUsageParser(), new CliModelRegistry());

        Assert.False(result.Ok);
        Assert.Equal("model is unavailable", result.Error);
    }
}
