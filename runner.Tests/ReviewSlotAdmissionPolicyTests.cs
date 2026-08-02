using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewSlotAdmissionPolicyTests
{
    [Theory]
    [InlineData(8.99, 6, 1.5, true)]
    [InlineData(9.00, 6, 1.5, false)]
    [InlineData(31.0, 6, 1.5, false)]
    public void New_slots_require_load_strictly_below_the_core_threshold(
        double load,
        int cores,
        double threshold,
        bool expected)
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(load, cores, activeSlots: 2),
            activeSlots: 2,
            slotCeiling: 4,
            threshold);

        Assert.Equal(expected, decision.Admitted);
        Assert.InRange(
            Math.Abs(load / cores - decision.LoadPerCore!.Value),
            0,
            0.001);
    }

    [Fact]
    public void Missing_load_or_core_evidence_fails_closed()
    {
        var missingLoad = ReviewSlotAdmissionPolicy.Decide(
            Sample(null, 6, activeSlots: 1),
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);
        var missingSample = ReviewSlotAdmissionPolicy.Decide(
            null,
            activeSlots: 1,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);

        Assert.False(missingLoad.Admitted);
        Assert.False(missingSample.Admitted);
        Assert.Contains("telemetry", missingLoad.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetry", missingSample.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Slot_ceiling_remains_a_hard_upper_bound()
    {
        var decision = ReviewSlotAdmissionPolicy.Decide(
            Sample(0.1, 6, activeSlots: 4),
            activeSlots: 4,
            slotCeiling: 4,
            maxLoadPerCore: 1.5);

        Assert.False(decision.Admitted);
        Assert.Contains("ceiling", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static HostTelemetrySample Sample(double? load, int cores, int activeSlots)
        => new(
            DateTime.UtcNow,
            CpuPercent: 75,
            Load1: load,
            Load5: load,
            Load15: load,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: 0,
            IoWaitPercent: 0,
            CpuCores: cores,
            ActiveSlots: activeSlots);
}
