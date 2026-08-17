using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-W34 slice S4 viewer boundary and request budget. The viewer cookie is
/// public authority, not a secret: what it has to guarantee is that it expires,
/// stays bounded in memory, and gives the rate limiter a stable subject.
/// </summary>
public sealed class PublicDemoViewerBoundaryTests
{
    [Theory]
    [InlineData(0, 5, true)]
    [InlineData(4, 5, true)]
    [InlineData(5, 5, false)]
    [InlineData(9, 5, false)]
    public void WithinAWindow_TheLimitIsTheCeiling(int alreadySeen, int limit, bool expected)
    {
        var window = new PublicDemoRateWindow(1_000, alreadySeen);
        var (_, allowed) = PublicDemoRateLimitPolicy.Evaluate(window, 1_500, 10_000, limit);
        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void AnExpiredWindow_RollsAndAdmitsAgain()
    {
        var exhausted = new PublicDemoRateWindow(1_000, 99);
        var (next, allowed) = PublicDemoRateLimitPolicy.Evaluate(exhausted, 11_000, 10_000, 5);
        Assert.True(allowed);
        Assert.Equal(11_000, next.StartedAtTicks);
        Assert.Equal(1, next.Count);
    }

    [Fact]
    public void ANonPositiveLimit_FailsClosed()
    {
        var (_, allowed) = PublicDemoRateLimitPolicy.Evaluate(new PublicDemoRateWindow(0, 0), 1, 10_000, 0);
        Assert.False(allowed);
    }

    [Fact]
    public void TheBudget_ShutsAViewerOffAtTheCeilingAndReopensNextMinute()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-17T10:00:00Z"));
        var budget = new PublicDemoRequestBudget(clock, requestsPerMinute: 3, maxTrackedViewers: 100);

        Assert.True(budget.TryConsume("viewer-a"));
        Assert.True(budget.TryConsume("viewer-a"));
        Assert.True(budget.TryConsume("viewer-a"));
        Assert.False(budget.TryConsume("viewer-a"));
        // One viewer's flood must not spend another viewer's budget.
        Assert.True(budget.TryConsume("viewer-b"));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(budget.TryConsume("viewer-a"));
    }

    [Fact]
    public void AViewerSession_IsIssuedOnceAndThenReused()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-17T10:00:00Z"));
        var sessions = new PublicDemoViewerSessions(clock, TimeSpan.FromMinutes(30), maxSessions: 100);

        var first = sessions.Resolve(null, out var issuedFirst);
        Assert.True(issuedFirst);
        Assert.NotEmpty(first);

        var second = sessions.Resolve(first, out var issuedSecond);
        Assert.False(issuedSecond);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AnExpiredOrForgedCookie_GetsAFreshEphemeralIdentity()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-17T10:00:00Z"));
        var sessions = new PublicDemoViewerSessions(clock, TimeSpan.FromMinutes(30), maxSessions: 100);
        var original = sessions.Resolve(null, out _);

        Assert.NotEqual(original, sessions.Resolve("forged-value", out var issuedForForged));
        Assert.True(issuedForForged);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(sessions.IsLive(original));
        Assert.NotEqual(original, sessions.Resolve(original, out var issuedAfterExpiry));
        Assert.True(issuedAfterExpiry);
    }

    [Fact]
    public void TheSessionMap_StaysUnderItsCeilingUnderAConnectionFlood()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-17T10:00:00Z"));
        var sessions = new PublicDemoViewerSessions(clock, TimeSpan.FromMinutes(30), maxSessions: 10);

        for (var i = 0; i < 200; i++) sessions.Resolve(null, out _);

        Assert.True(sessions.Count <= 10, $"viewer map grew to {sessions.Count}");
    }

    [Theory]
    [InlineData("demo-app", true)]
    [InlineData("DEMO-APP", true)]
    [InlineData("demo-platform", true)]
    [InlineData("agent-taskboard", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheProjectScope_AdmitsOnlyAnnouncedDemoProjects(string? handle, bool expected)
        => Assert.Equal(expected, PublicDemoProjectScope.Allows(["demo-app", "demo-platform"], handle));
}
