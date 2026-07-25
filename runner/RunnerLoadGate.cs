namespace AgentRunner;

/// <summary>
/// Sustained load/core admission invariant for a remote host. Existing slots
/// keep running; only new claims stop while the normalized load remains high.
/// </summary>
public sealed class RunnerLoadGate
{
    private readonly double _threshold;
    private readonly TimeSpan _sustainedFor;
    private DateTime? _highSince;
    private double _lastNormalized;
    private bool _reported;

    public RunnerLoadGate(double threshold = 1.5, TimeSpan? sustainedFor = null)
    {
        _threshold = threshold;
        _sustainedFor = sustainedFor ?? TimeSpan.FromMinutes(2);
    }

    public RunnerLoadGateDecision Observe(HostTelemetrySample? sample, DateTime now)
    {
        if (sample?.Load1 is not { } load || sample.CpuCores <= 0)
        {
            return Decide(now);
        }

        var normalized = load / sample.CpuCores;
        _lastNormalized = normalized;
        if (normalized <= _threshold)
        {
            _highSince = null;
            _reported = false;
            return new(false, normalized, TimeSpan.Zero, false);
        }

        _highSince ??= now;
        return Decide(now);
    }

    private RunnerLoadGateDecision Decide(DateTime now)
    {
        if (_highSince is null)
            return new(false, _lastNormalized, TimeSpan.Zero, false);
        var duration = now - _highSince.Value;
        var throttled = duration >= _sustainedFor;
        var emit = throttled && !_reported;
        if (emit) _reported = true;
        return new(throttled, _lastNormalized, duration, emit);
    }
}

public sealed record RunnerLoadGateDecision(
    bool Throttle,
    double LoadPerCore,
    TimeSpan SustainedFor,
    bool EmitEvent);
