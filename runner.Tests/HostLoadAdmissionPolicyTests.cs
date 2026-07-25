using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostLoadAdmissionPolicyTests
{
    [Fact]
    public void Load_per_core_above_threshold_refuses_new_claim()
    {
        var sample = Sample(load1: 178, cores: 12);

        var decision = HostLoadAdmissionPolicy.Decide(sample, maxLoadPerCore: 1.5);

        Assert.False(decision.Admitted);
        Assert.Equal(178d / 12d, decision.LoadPerCore);
    }

    [Fact]
    public void Load_per_core_at_threshold_allows_claim()
    {
        var decision = HostLoadAdmissionPolicy.Decide(
            Sample(load1: 18, cores: 12),
            maxLoadPerCore: 1.5);

        Assert.True(decision.Admitted);
    }

    private static HostTelemetrySample Sample(double load1, int cores)
        => new(
            DateTime.UtcNow,
            CpuPercent: null,
            Load1: load1,
            Load5: null,
            Load15: null,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: null,
            IoWaitPercent: null,
            CpuCores: cores,
            ActiveSlots: 0);
}
