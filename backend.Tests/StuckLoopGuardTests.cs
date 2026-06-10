

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of the auto-loop circuit breaker. The guard is the
/// load-bearing rule that lets the orchestrator keep working a task to
/// completion (the user's stated product goal) without burning unbounded
/// CLI quota when the agent gets stuck in a question-loop.
/// </summary>
public class StuckLoopGuardTests
{
    private static readonly DateTime T0 = new(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Empty_StartsAtZero()
    {
        var s = StuckLoopGuard.Empty(T0);
        Assert.Equal(0, s.IterationCount);
        Assert.Equal(0L, s.CumulativeOrchestratorTokens);
        Assert.Equal(T0, s.FirstAt);
        Assert.Null(s.LastQuestion);
    }

    [Fact]
    public void Next_FromNull_BehavesLikeEmpty()
    {
        var s = StuckLoopGuard.Next(prior: null, usage: null, question: "q", reply: "r", error: null, now: T0);
        Assert.Equal(1, s.IterationCount);
        Assert.Equal(0L, s.CumulativeOrchestratorTokens);
        Assert.Equal("q", s.LastQuestion);
        Assert.Equal("r", s.LastReply);
    }

    [Fact]
    public void Next_AccumulatesBillableTokens_ExcludesCacheRead()
    {
        // Cache-read tokens are NOT counted - subscription quota does not
        // bill for them, so we should not budget against them either.
        var usage1 = new OrchestratorTokenUsage
        {
            InputTokens = 100, OutputTokens = 50, CacheReadTokens = 9000, CacheCreationTokens = 25
        };
        var s1 = StuckLoopGuard.Next(null, usage1, "q1", "r1", null, T0);
        Assert.Equal(175L, s1.CumulativeOrchestratorTokens);

        var usage2 = new OrchestratorTokenUsage
        {
            InputTokens = 200, OutputTokens = 100, CacheReadTokens = 12000, CacheCreationTokens = 0
        };
        var s2 = StuckLoopGuard.Next(s1, usage2, "q2", "r2", null, T0.AddMinutes(1));
        Assert.Equal(2, s2.IterationCount);
        Assert.Equal(175L + 300L, s2.CumulativeOrchestratorTokens);
        Assert.Equal("q2", s2.LastQuestion);
    }

    [Fact]
    public void Decide_ContinuesUnderBudget()
    {
        var s = StuckLoopGuard.Empty(T0) with { IterationCount = 2, CumulativeOrchestratorTokens = 1_000 };
        Assert.Equal(StuckLoopVerdict.Continue, StuckLoopGuard.Decide(s, StuckLoopBudget.Default));
    }

    [Fact]
    public void Decide_BreaksAtIterationCap()
    {
        var s = StuckLoopGuard.Empty(T0) with { IterationCount = 5, CumulativeOrchestratorTokens = 10 };
        Assert.Equal(StuckLoopVerdict.CircuitBreak, StuckLoopGuard.Decide(s, StuckLoopBudget.Default));
    }

    [Fact]
    public void Decide_BreaksAtTokenCap()
    {
        var s = StuckLoopGuard.Empty(T0) with { IterationCount = 1, CumulativeOrchestratorTokens = 200_000 };
        Assert.Equal(StuckLoopVerdict.CircuitBreak, StuckLoopGuard.Decide(s, StuckLoopBudget.Default));
    }

    [Fact]
    public void Decide_RespectsCustomBudget()
    {
        var tight = new StuckLoopBudget(MaxIterations: 2, MaxOrchestratorTokens: 100);
        var s1 = StuckLoopGuard.Empty(T0) with { IterationCount = 2, CumulativeOrchestratorTokens = 50 };
        Assert.Equal(StuckLoopVerdict.CircuitBreak, StuckLoopGuard.Decide(s1, tight));

        var s2 = StuckLoopGuard.Empty(T0) with { IterationCount = 1, CumulativeOrchestratorTokens = 99 };
        Assert.Equal(StuckLoopVerdict.Continue, StuckLoopGuard.Decide(s2, tight));

        var s3 = StuckLoopGuard.Empty(T0) with { IterationCount = 1, CumulativeOrchestratorTokens = 100 };
        Assert.Equal(StuckLoopVerdict.CircuitBreak, StuckLoopGuard.Decide(s3, tight));
    }

    [Fact]
    public void FormatBreakerMessage_NamesBothCeilings()
    {
        var s = StuckLoopGuard.Empty(T0) with { IterationCount = 5, CumulativeOrchestratorTokens = 12_345 };
        var msg = StuckLoopGuard.FormatBreakerMessage(s, StuckLoopBudget.Default);
        Assert.Contains("circuit-breaker", msg);
        Assert.Contains("5/5", msg);
        Assert.Contains("12,345", msg);
        Assert.Contains("200,000", msg);
    }
}
