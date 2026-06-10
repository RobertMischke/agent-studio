
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Unit coverage for the pure <see cref="RapidCrashBreaker"/> decider — the
/// rapid-crash governor that closes the gap between the quarantine breaker
/// (excludes launch-shaped kinds) and the cross-slug breaker (dedupes a slug).
/// </summary>
public sealed class RapidCrashBreakerTests
{
    [Theory]
    [InlineData("failed", 0.2, 0, true)]   // the incident shape: exit fast, no commit
    [InlineData("failed", 7.9, 0, true)]   // just under the window
    [InlineData("failed", 8.0, 0, false)]  // at the window: not rapid
    [InlineData("failed", 30.0, 0, false)] // slow failure: a real attempt, not rapid
    [InlineData("failed", 0.2, 1, false)]  // fast but committed: progress, not rapid
    [InlineData("completed", 0.2, 0, false)] // fast success is never a crash
    [InlineData("stopped", 0.2, 0, false)]   // deliberate stop is never a crash
    public void IsRapidCrash_classifies_by_status_duration_and_commits(
        string status, double durationSeconds, int commits, bool expected)
    {
        Assert.Equal(expected, RapidCrashBreaker.IsRapidCrash(status, durationSeconds, commits));
    }

    [Fact]
    public void Backoff_grows_exponentially_and_caps()
    {
        Assert.Equal(System.TimeSpan.Zero, RapidCrashBreaker.Backoff(0));
        Assert.Equal(15, RapidCrashBreaker.Backoff(1).TotalSeconds, 3);
        Assert.Equal(60, RapidCrashBreaker.Backoff(2).TotalSeconds, 3);
        Assert.Equal(240, RapidCrashBreaker.Backoff(3).TotalSeconds, 3);
        // Capped at 15 minutes no matter how high the streak climbs.
        Assert.Equal(900, RapidCrashBreaker.Backoff(99).TotalSeconds, 3);
    }

    [Fact]
    public void Backoff_is_monotonic_nondecreasing()
    {
        var prev = RapidCrashBreaker.Backoff(1);
        for (var n = 2; n <= 20; n++)
        {
            var cur = RapidCrashBreaker.Backoff(n);
            Assert.True(cur >= prev, $"backoff must not shrink at n={n}");
            prev = cur;
        }
    }
}
