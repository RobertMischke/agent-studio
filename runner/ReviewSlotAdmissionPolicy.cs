namespace AgentRunner;

public sealed record ReviewSlotAdmissionDecision(
    bool Admitted,
    string Reason,
    double? LoadPerCore);

/// <summary>
/// Immediate load-aware admission for new Review Executor slots. This policy
/// never owns or cancels an active review; it decides only whether the daemon
/// may ask the Task Server for one more attempt.
/// </summary>
public static class ReviewSlotAdmissionPolicy
{
    public static ReviewSlotAdmissionDecision Decide(
        HostTelemetrySample? sample,
        int activeSlots,
        int slotCeiling,
        double maxLoadPerCore)
    {
        if (activeSlots >= slotCeiling)
            return new(false, $"slot ceiling reached ({activeSlots}/{slotCeiling})", null);
        if (sample?.Load1 is not { } load || sample.CpuCores <= 0)
            return new(false, "current load telemetry is unavailable", null);

        var normalized = load / sample.CpuCores;
        if (normalized >= maxLoadPerCore)
        {
            return new(
                false,
                $"load/core {normalized:0.00} is at or above {maxLoadPerCore:0.00}; "
                + $"cpu={Percent(sample.CpuPercent)} steal={Percent(sample.CpuStealPercent)}",
                normalized);
        }
        return new(
            true,
            $"load/core {normalized:0.00} is below {maxLoadPerCore:0.00}",
            normalized);
    }

    private static string Percent(double? value)
        => value is null ? "unknown" : $"{value:0.0}%";
}
