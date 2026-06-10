using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the parse contract for both CLIs: a representative <c>usage</c>
/// frame round-trips into <see cref="ParsedTurnUsage"/> with the right
/// tokens AND with a populated <c>contextWindow</c> snapshot when the
/// model is known to the registry.
/// </summary>
public class CliUsageParserTests
{
    private static readonly CliModelRegistry Registry = new();

    [Fact]
    public void ClaudeParser_ExtractsTokensAndContextWindow()
    {
        var parser = new ClaudeUsageParser();
        // Real shape from `claude -p --output-format=json`.
        var frame = JsonDocument.Parse("""
        {
          "type": "result",
          "subtype": "success",
          "is_error": false,
          "result": "ok",
          "session_id": "abc-123",
          "model": "claude-sonnet-4-6",
          "usage": {
            "input_tokens": 1200,
            "output_tokens": 350,
            "cache_read_input_tokens": 65000,
            "cache_creation_input_tokens": 4000
          }
        }
        """).RootElement;

        Assert.True(parser.TryParse(frame, modelHint: null, Registry, out var usage));
        Assert.Equal("claude-sonnet-4-6", usage.Model);
        Assert.Equal(1200, usage.Input);
        Assert.Equal(350, usage.Output);
        Assert.Equal(65000, usage.CacheRead);
        Assert.Equal(4000, usage.CacheWrite);
        Assert.NotNull(usage.ContextWindow);
        Assert.Equal(200_000, usage.ContextWindow!.TotalSize);
        Assert.Equal(66200, usage.ContextWindow.Used);
        Assert.Equal(200_000 - 66200, usage.ContextWindow.Remaining);
    }

    [Fact]
    public void ClaudeParser_FallsBackToModelHintWhenFrameOmitsModel()
    {
        var parser = new ClaudeUsageParser();
        var frame = JsonDocument.Parse("""
        { "usage": { "input_tokens": 10, "output_tokens": 5 } }
        """).RootElement;

        Assert.True(parser.TryParse(frame, modelHint: "claude-haiku-4-5", Registry, out var usage));
        Assert.Equal("claude-haiku-4-5", usage.Model);
        Assert.Equal(200_000, usage.ContextWindow!.TotalSize);
    }

    [Fact]
    public void CodexParser_OnlyMatchesTurnCompleted()
    {
        var parser = new CodexUsageParser();

        var turnStarted = JsonDocument.Parse("""{ "type": "turn.started" }""").RootElement;
        Assert.False(parser.TryParse(turnStarted, modelHint: null, Registry, out _));

        var turnCompleted = JsonDocument.Parse("""
        {
          "type": "turn.completed",
          "model": "gpt-5-codex",
          "usage": {
            "input_tokens": 9000,
            "cached_input_tokens": 7200,
            "output_tokens": 800,
            "reasoning_output_tokens": 240
          }
        }
        """).RootElement;

        Assert.True(parser.TryParse(turnCompleted, modelHint: null, Registry, out var usage));
        Assert.Equal("gpt-5-codex", usage.Model);
        Assert.Equal(9000, usage.Input);
        Assert.Equal(7200, usage.CacheRead);
        Assert.Equal(800, usage.Output);
        Assert.Equal(240, usage.ReasoningOutput);
        Assert.Equal(272_000, usage.ContextWindow!.TotalSize);
        Assert.Equal(16200, usage.ContextWindow.Used);
    }

    [Fact]
    public void ParserRegistry_DispatchesByCliType()
    {
        var registry = new CliUsageParserRegistry(new ICliUsageParser[]
        {
            new ClaudeUsageParser(),
            new CodexUsageParser(),
        });

        Assert.IsType<ClaudeUsageParser>(registry.Get("claude"));
        Assert.IsType<CodexUsageParser>(registry.Get("codex"));
        Assert.IsType<ClaudeUsageParser>(registry.Get("Claude"));
        Assert.Null(registry.Get("copilot"));
        Assert.Null(registry.Get(null));
    }

    [Fact]
    public void ModelRegistry_PrefixMatchesDatedModelIds()
    {
        Assert.Equal(200_000, Registry.TotalContextSize("claude-sonnet-4-6-20260301"));
        Assert.Null(Registry.TotalContextSize("nonexistent-model"));
        Assert.Null(Registry.TotalContextSize(null));
    }
}
