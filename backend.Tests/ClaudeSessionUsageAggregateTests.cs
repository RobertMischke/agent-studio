
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the post-hoc token aggregation that reconstructs a Claude run's
/// total spend from its session transcript (ASS-626 / ASS-665). The Claude
/// CLI never reports a terminal usage footer, and a killed run loses even
/// the final result frame, so the per-turn <c>message.usage</c> blocks in
/// the JSONL are the only durable record. The summation must add every
/// assistant turn - not take the last one - or a long, cache-heavy run
/// (13.5M tokens in the proof case) reads back as a fraction of its real cost.
/// </summary>
public class ClaudeSessionUsageAggregateTests
{
    private static string AssistantTurn(
        long input, long output, long cacheRead, long cacheCreation,
        string model = "claude-opus-4-8", string ts = "2026-06-04T10:00:00Z")
    {
        var usage = "{\"input_tokens\":" + input
            + ",\"output_tokens\":" + output
            + ",\"cache_read_input_tokens\":" + cacheRead
            + ",\"cache_creation_input_tokens\":" + cacheCreation + "}";
        return "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"message\":{\"model\":\""
            + model + "\",\"usage\":" + usage + "}}";
    }

    [Fact]
    public void AggregateUsageFromLines_SumsEveryAssistantTurn()
    {
        var lines = new[]
        {
            AssistantTurn(input: 1000, output: 40000, cacheRead: 4_000_000, cacheCreation: 5000),
            AssistantTurn(input: 1200, output: 48000, cacheRead: 4_500_000, cacheCreation: 4000),
            AssistantTurn(input: 1300, output: 40000, cacheRead: 4_900_000, cacheCreation: 3000),
        };

        var agg = ClaudeSessionInspector.AggregateUsageFromLines(lines);

        Assert.Equal(3500, agg.InputTokens);
        Assert.Equal(128000, agg.OutputTokens);
        Assert.Equal(13_400_000, agg.CacheReadTokens);
        Assert.Equal(12000, agg.CacheCreationTokens);
        Assert.Equal(3500 + 128000 + 13_400_000 + 12000, agg.TotalTokens);
        Assert.Equal(3, agg.TurnCount);
        Assert.Equal("claude-opus-4-8", agg.Model);
    }

    [Fact]
    public void AggregateUsageFromLines_IgnoresNonAssistantAndMalformedLines()
    {
        var lines = new[]
        {
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
            "not json at all",
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\"}",
            AssistantTurn(input: 500, output: 100, cacheRead: 2000, cacheCreation: 0),
            "",
            // Assistant frame with no usage block (e.g. a tool-result echo) is skipped.
            "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-8\"}}",
        };

        var agg = ClaudeSessionInspector.AggregateUsageFromLines(lines);

        Assert.Equal(1, agg.TurnCount);
        Assert.Equal(500, agg.InputTokens);
        Assert.Equal(100, agg.OutputTokens);
        Assert.Equal(2000, agg.CacheReadTokens);
        Assert.Equal(2600, agg.TotalTokens);
    }

    [Fact]
    public void AggregateUsageFromLines_EmptyTranscript_ReturnsZeroAggregate()
    {
        var agg = ClaudeSessionInspector.AggregateUsageFromLines(System.Array.Empty<string>());

        Assert.Equal(0, agg.TurnCount);
        Assert.Equal(0, agg.TotalTokens);
        Assert.Null(agg.Model);
    }

    [Fact]
    public void AggregateUsageFromLines_ToleratesMissingUsageFields()
    {
        // A turn that only reports output tokens (no cache fields) must not throw
        // and must contribute exactly what it carries.
        var lines = new[]
        {
            "{\"type\":\"assistant\",\"timestamp\":\"2026-06-04T10:00:00Z\",\"message\":{\"model\":\"m\",\"usage\":{\"output_tokens\":42}}}",
        };

        var agg = ClaudeSessionInspector.AggregateUsageFromLines(lines);

        Assert.Equal(1, agg.TurnCount);
        Assert.Equal(0, agg.InputTokens);
        Assert.Equal(42, agg.OutputTokens);
        Assert.Equal(0, agg.CacheReadTokens);
        Assert.Equal(42, agg.TotalTokens);
    }

    [Fact]
    public void FormatUsageString_RendersCompactBreakdown()
    {
        var agg = new ClaudeSessionUsageAggregate(
            Model: "claude-opus-4-8",
            InputTokens: 47_500,
            OutputTokens: 128_000,
            CacheReadTokens: 13_400_000,
            CacheCreationTokens: 12_000,
            TotalTokens: 13_587_500,
            TurnCount: 40,
            LastTurnAt: null);

        var s = ClaudeSessionInspector.FormatUsageString(agg);

        Assert.Equal("13.6M tokens (in 47.5k, out 128k, cache-read 13.4M, cache-write 12k)", s);
    }

    [Fact]
    public void FormatUsageString_OmitsZeroCacheFields()
    {
        var agg = new ClaudeSessionUsageAggregate(
            Model: "m", InputTokens: 500, OutputTokens: 100,
            CacheReadTokens: 0, CacheCreationTokens: 0,
            TotalTokens: 600, TurnCount: 1, LastTurnAt: null);

        var s = ClaudeSessionInspector.FormatUsageString(agg);

        Assert.Equal("600 tokens (in 500, out 100)", s);
    }
}
