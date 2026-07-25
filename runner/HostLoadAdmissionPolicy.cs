namespace AgentRunner;

/// <summary>Pure load-per-core admission gate for new remote coding claims.</summary>
public static class HostLoadAdmissionPolicy
{
    public static HostLoadAdmissionDecision Decide(
        HostTelemetrySample? sample,
        double maxLoadPerCore)
    {
        if (sample?.Load1 is not { } load || sample.CpuCores <= 0 || maxLoadPerCore <= 0)
            return new HostLoadAdmissionDecision(true, null, maxLoadPerCore);

        var loadPerCore = load / sample.CpuCores;
        return new HostLoadAdmissionDecision(
            loadPerCore <= maxLoadPerCore,
            loadPerCore,
            maxLoadPerCore);
    }
}

public sealed record HostLoadAdmissionDecision(
    bool Admitted,
    double? LoadPerCore,
    double Threshold);
