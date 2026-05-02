using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the per-project rollup math: the orchestrator-log entries
/// in, total amounts + per-model breakdown + theoretical API cost
/// out. Same matrix style as <c>RunOutcomePolicyTests</c>.
/// </summary>
public class TokenSummaryTests
{
    private static OrchestratorLogEntry Entry(string model, long input, long output, long cacheRead = 0, long cacheCreate = 0)
        => new()
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.General,
            Summary = "test entry",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = (int)input,
                OutputTokens = (int)output,
                CacheReadTokens = (int)cacheRead,
                CacheCreationTokens = (int)cacheCreate
            }
        };

    [Fact]
    public void Summarize_NoEntries_ReturnsZeros()
    {
        var s = TokenSummaryService.Summarize("Demo", Array.Empty<OrchestratorLogEntry>());
        Assert.Equal("Demo", s.Project);
        Assert.Equal(0, s.OrchestratorLlmCalls);
        Assert.Equal(0L, s.TotalInputTokens);
        Assert.Equal(0m, s.EstimatedApiCostUsd);
        Assert.Empty(s.ByModel);
    }

    [Fact]
    public void Summarize_EntriesWithoutTokenUsage_DontInflateLlmCallCount()
    {
        // OrchestratorLogEntry without tokenUsage represents an action
        // the runner took without an LLM call (queued follow-up,
        // watchdog kill). Those count toward "entries" but not "LLM calls".
        var entries = new[]
        {
            new OrchestratorLogEntry { Summary = "no usage" },
            Entry("claude-haiku-4-5", 1_000, 100)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);
        Assert.Equal(2, s.OrchestratorEntries);
        Assert.Equal(1, s.OrchestratorLlmCalls);
    }

    [Fact]
    public void Summarize_AggregatesPerModel_WithCorrectCost()
    {
        var entries = new[]
        {
            Entry("claude-opus-4-7", 100_000, 10_000),
            Entry("claude-opus-4-7",  50_000,  5_000),
            Entry("claude-haiku-4-5", 200_000, 20_000)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);

        Assert.Equal(3, s.OrchestratorLlmCalls);
        Assert.Equal(350_000L, s.TotalInputTokens);
        Assert.Equal(35_000L, s.TotalOutputTokens);

        // Opus: 150K in @ $5/M = $0.75; 15K out @ $25/M = $0.375; total $1.125
        // Haiku: 200K in @ $1/M = $0.20; 20K out @ $5/M = $0.10; total $0.30
        // Grand: $1.425
        Assert.Equal(1.425m, s.EstimatedApiCostUsd);
        Assert.True(s.AllModelsPriced);

        Assert.Equal(2, s.ByModel.Count);
        var opus = s.ByModel.Single(m => m.Model == "claude-opus-4-7");
        Assert.Equal(2, opus.Calls);
        Assert.Equal(150_000L, opus.InputTokens);
        Assert.Equal(1.125m, opus.EstimatedApiCostUsd);
    }

    [Fact]
    public void Summarize_UnknownModel_FlagsNotAllPriced()
    {
        var entries = new[]
        {
            Entry("claude-opus-4-7", 1_000, 100),
            Entry("gpt-5", 1_000, 100)
        };
        var s = TokenSummaryService.Summarize("Demo", entries);
        Assert.False(s.AllModelsPriced);
        var unknown = s.ByModel.Single(m => m.Model == "gpt-5");
        Assert.False(unknown.ModelPriced);
        Assert.Equal(0m, unknown.EstimatedApiCostUsd);
    }

    [Fact]
    public void Summarize_DisclaimerIsAlwaysPresent()
    {
        var s = TokenSummaryService.Summarize("Demo", Array.Empty<OrchestratorLogEntry>());
        Assert.Contains("subscription", s.Disclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comparison", s.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }
}
