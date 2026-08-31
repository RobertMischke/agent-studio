using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaFreshnessPolicyTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_ReadingWithinTtl_IsFresh()
    {
        var result = QuotaFreshnessPolicy.Evaluate(
            Snapshot(),
            TimeSpan.FromMinutes(10),
            CapturedAt.AddMinutes(9));

        Assert.False(result.IsStale);
        Assert.Equal(540, result.AgeSeconds);
        Assert.Null(result.StaleSince);
    }

    [Fact]
    public void Evaluate_ReadingBeyondTtl_IsStaleFromTtlBoundary()
    {
        var result = QuotaFreshnessPolicy.Evaluate(
            Snapshot(),
            TimeSpan.FromMinutes(10),
            CapturedAt.AddMinutes(12));

        Assert.True(result.IsStale);
        Assert.Equal(720, result.AgeSeconds);
        Assert.Equal(CapturedAt.AddMinutes(10), result.StaleSince);
    }

    [Fact]
    public void Evaluate_FailedProbe_IsStaleFromFailureTime()
    {
        var failedAt = CapturedAt.AddMinutes(2);
        var result = QuotaFreshnessPolicy.Evaluate(
            Snapshot() with { ProbeFailedAt = failedAt },
            TimeSpan.FromMinutes(10),
            failedAt.AddSeconds(1));

        Assert.True(result.IsStale);
        Assert.Equal(failedAt, result.StaleSince);
    }

    private static QuotaSnapshot Snapshot() => new()
    {
        CliType = "codex",
        FetchedAt = CapturedAt,
        CapturedAt = CapturedAt,
        Plan = "Pro",
        Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
    };
}
