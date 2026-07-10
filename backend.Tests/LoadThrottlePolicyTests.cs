using AgentStudio.Runner;
using Xunit;

namespace AgentTaskboard.Tests;

public sealed class LoadThrottlePolicyTests
{
    [Fact]
    public void SingleSpike_DoesNotThrottle()
    {
        var now = new DateTime(2026, 7, 11, 1, 10, 0, DateTimeKind.Utc);
        var decision = LoadThrottlePolicy.Decide(new[] { new CpuLoadSample(now, 100) }, now);
        Assert.False(decision.Throttle);
    }

    [Fact]
    public void ContinuousSaturationForOneMinute_Throttles()
    {
        var now = new DateTime(2026, 7, 11, 1, 10, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 5)
            .Select(i => new CpuLoadSample(now.AddSeconds(-60 + i * 15), 91 + i))
            .ToArray();
        var decision = LoadThrottlePolicy.Decide(samples, now);
        Assert.True(decision.Throttle);
        Assert.Equal(TimeSpan.FromMinutes(1), decision.SustainedFor);
        Assert.Contains("load-throttle", decision.Reason);
    }

    [Fact]
    public void CoolingSample_ImmediatelyReopensAdmission()
    {
        var now = new DateTime(2026, 7, 11, 1, 10, 0, DateTimeKind.Utc);
        var samples = new[]
        {
            new CpuLoadSample(now.AddSeconds(-75), 99),
            new CpuLoadSample(now.AddSeconds(-60), 99),
            new CpuLoadSample(now.AddSeconds(-45), 99),
            new CpuLoadSample(now.AddSeconds(-30), 99),
            new CpuLoadSample(now.AddSeconds(-15), 99),
            new CpuLoadSample(now, 70),
        };
        Assert.False(LoadThrottlePolicy.Decide(samples, now).Throttle);
    }

    [Fact]
    public void ExactlyThreshold_IsNotSaturated()
    {
        var now = DateTime.UtcNow;
        var samples = new[] { new CpuLoadSample(now.AddMinutes(-1), 90), new CpuLoadSample(now, 90) };
        Assert.False(LoadThrottlePolicy.Decide(samples, now).Throttle);
    }
}
