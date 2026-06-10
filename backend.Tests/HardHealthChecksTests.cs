
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the predicates that decide when the supervisor must speak up. Each
/// check is intentionally simple so the rules can be reasoned about line by
/// line; the cost of a false positive is one extra advisory log line.
/// </summary>
public class HardHealthChecksTests
{
    private static SupervisorObservation Obs(
        string project = "p",
        string? jobId = "j",
        DateTime? lastProgressAt = null,
        DateTime? capturedAt = null,
        SupervisorErrorCounts? errors = null,
        SupervisorQuotaWindow? quota = null,
        IReadOnlyList<string>? samples = null) =>
        new(
            CapturedAt: capturedAt ?? new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc),
            Project: project,
            RunnerStatus: "auto-continuous (active)",
            CurrentJobId: jobId,
            CurrentRunState: "3-progress",
            LastProgressAt: lastProgressAt,
            Quota: quota,
            RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
            RecentAgentSamples: samples ?? Array.Empty<string>(),
            ErrorCounts: errors ?? new SupervisorErrorCounts(0, 0, 0));

    [Fact]
    public void NoProgress_FiresWhenAgeExceedsThreshold()
    {
        var captured = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var obs = Obs(capturedAt: captured, lastProgressAt: captured.AddMinutes(-15));
        var advisory = HardHealthChecks.NoProgress(obs, TimeSpan.FromMinutes(10));
        Assert.NotNull(advisory);
        Assert.Equal("no-progress", advisory!.Topic);
        Assert.Equal(SupervisorSeverity.Warn, advisory.Severity);
    }

    [Fact]
    public void NoProgress_DoesNotFire_WhenWithinThreshold()
    {
        var captured = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var obs = Obs(capturedAt: captured, lastProgressAt: captured.AddMinutes(-5));
        Assert.Null(HardHealthChecks.NoProgress(obs, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void NoProgress_DoesNotFire_WhenIdle()
    {
        var obs = Obs(jobId: null, lastProgressAt: null);
        Assert.Null(HardHealthChecks.NoProgress(obs, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void ErrorBurst_FiresOnlyWhenSumExceedsThreshold()
    {
        var below = Obs(errors: new SupervisorErrorCounts(2, 1, 0));
        Assert.Null(HardHealthChecks.ErrorBurst(below, threshold: 5));

        var atOrAbove = Obs(errors: new SupervisorErrorCounts(3, 2, 1));
        var advisory = HardHealthChecks.ErrorBurst(atOrAbove, threshold: 5);
        Assert.NotNull(advisory);
        Assert.Equal("error-burst", advisory!.Topic);
    }

    [Fact]
    public void QuotaCritical_FiresWhenUsedAtOrAboveFraction()
    {
        var fine = Obs(quota: new SupervisorQuotaWindow("claude", 0.50, null));
        Assert.Null(HardHealthChecks.QuotaCritical(fine, 0.95));

        var critical = Obs(quota: new SupervisorQuotaWindow("claude", 0.97, null));
        var advisory = HardHealthChecks.QuotaCritical(critical, 0.95);
        Assert.NotNull(advisory);
        Assert.Equal(SupervisorSeverity.High, advisory!.Severity);
    }

    [Fact]
    public void QuotaCritical_NoQuotaInfo_NoAdvisory()
    {
        var obs = Obs(quota: null);
        Assert.Null(HardHealthChecks.QuotaCritical(obs, 0.95));
    }

    [Fact]
    public void ToolCallRepeat_FiresOnRepeatedSample()
    {
        var samples = Enumerable.Repeat("Reading file foo.cs", 6).ToList();
        var obs = Obs(samples: samples);
        var advisory = HardHealthChecks.ToolCallRepeat(obs, maxRepeat: 5);
        Assert.NotNull(advisory);
        Assert.Equal("tool-call-repeat", advisory!.Topic);
    }

    [Fact]
    public void ToolCallRepeat_DoesNotFire_OnVariedSamples()
    {
        var samples = new List<string> { "a", "b", "c", "d", "e", "f" };
        var obs = Obs(samples: samples);
        Assert.Null(HardHealthChecks.ToolCallRepeat(obs, maxRepeat: 2));
    }

    [Fact]
    public void RunAll_AggregatesAllAdvisories()
    {
        var captured = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var obs = Obs(
            capturedAt: captured,
            lastProgressAt: captured.AddMinutes(-30),
            errors: new SupervisorErrorCounts(10, 0, 1),
            quota: new SupervisorQuotaWindow("claude", 0.99, null),
            samples: Enumerable.Repeat("repeat", 10).ToList());

        var advisories = HardHealthChecks.RunAll(obs, HardCheckThresholds.Defaults()).ToList();
        Assert.Equal(4, advisories.Count);
        Assert.Contains(advisories, a => a.Topic == "no-progress");
        Assert.Contains(advisories, a => a.Topic == "error-burst");
        Assert.Contains(advisories, a => a.Topic == "quota-critical");
        Assert.Contains(advisories, a => a.Topic == "tool-call-repeat");
    }

    [Fact]
    public void Thresholds_Defaults_AreSane()
    {
        var d = HardCheckThresholds.Defaults();
        Assert.True(d.NoProgressThreshold > TimeSpan.Zero);
        Assert.True(d.ErrorBurstThreshold > 0);
        Assert.InRange(d.QuotaCriticalFraction, 0.0, 1.0);
        Assert.True(d.ToolCallRepeatLimit > 0);
    }
}
