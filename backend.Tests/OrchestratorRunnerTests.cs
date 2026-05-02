using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the JSON-envelope parser <see cref="OrchestratorRunner"/> uses
/// to read <c>claude -p ... --output-format json</c> output. The CLI's
/// envelope shape is the only contract we depend on; pinning it here
/// keeps the runner safe against accidental field renames in a
/// <see cref="ParseResult"/> refactor.
/// </summary>
public class OrchestratorRunnerTests
{
    [Fact]
    public void ParseResult_HappyPath_ExtractsTextAndUsage()
    {
        // Real shape captured from `claude -p "..." --output-format json --model claude-haiku-4-5`.
        var json = """
        {
          "type": "result",
          "subtype": "success",
          "is_error": false,
          "duration_ms": 4321,
          "duration_api_ms": 4100,
          "num_turns": 1,
          "result": "Continue with the existing chat compose box implementation.",
          "session_id": "a1b2c3d4-e5f6-4789-abcd-ef0123456789",
          "total_cost_usd": 0.0123,
          "model": "claude-haiku-4-5",
          "usage": {
            "input_tokens": 1234,
            "cache_creation_input_tokens": 56,
            "cache_read_input_tokens": 789,
            "output_tokens": 42
          }
        }
        """;

        var result = OrchestratorRunner.ParseResult(json, "claude-haiku-4-5");

        Assert.True(result.Success);
        Assert.Equal("Continue with the existing chat compose box implementation.", result.ReplyText);
        Assert.Equal("claude-haiku-4-5", result.Model);
        Assert.NotNull(result.TokenUsage);
        Assert.Equal(1234, result.TokenUsage!.InputTokens);
        Assert.Equal(42, result.TokenUsage.OutputTokens);
        Assert.Equal(789, result.TokenUsage.CacheReadTokens);
        Assert.Equal(56, result.TokenUsage.CacheCreationTokens);
    }

    [Fact]
    public void ParseResult_IsErrorTrue_FlagsFailure()
    {
        var json = """{ "type": "result", "is_error": true, "result": "rate limit", "usage": { "input_tokens": 0, "output_tokens": 0 } }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.False(result.Success);
        Assert.Equal("rate limit", result.ReplyText);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseResult_EmptyResultText_FlagsFailure()
    {
        var json = """{ "type": "result", "is_error": false, "result": "", "usage": { "input_tokens": 1, "output_tokens": 0 } }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.False(result.Success);
    }

    [Fact]
    public void ParseResult_MissingUsage_StillReturnsText()
    {
        var json = """{ "type": "result", "is_error": false, "result": "ok" }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.True(result.Success);
        Assert.Equal("ok", result.ReplyText);
        Assert.Null(result.TokenUsage);
    }

    [Fact]
    public void ParseResult_GarbageInput_FlagsFailure()
    {
        var result = OrchestratorRunner.ParseResult("not json at all", "claude-opus-4-7");
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseResult_EmptyStdout_FlagsFailure()
    {
        var result = OrchestratorRunner.ParseResult("", "claude-opus-4-7");
        Assert.False(result.Success);
    }
}
