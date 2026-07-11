namespace AgentStudio.Runner;

public sealed record CpuLoadSample(DateTime AtUtc, double UsedPercent);

public sealed record LoadThrottleDecision(bool Throttle, double CurrentPercent, TimeSpan SustainedFor)
{
    public string Reason => Throttle
        ? $"load-throttle: system CPU {CurrentPercent:0.#}% has remained above the saturation threshold for {SustainedFor.TotalSeconds:0}s"
        : $"system CPU {CurrentPercent:0.#}% is below the sustained-load gate";
}

/// <summary>Pure policy for host-load admission. A single spike never closes admission.</summary>
public static class LoadThrottlePolicy
{
    public static LoadThrottleDecision Decide(
        IReadOnlyList<CpuLoadSample> samples,
        DateTime nowUtc,
        double thresholdPercent = 90,
        TimeSpan? requiredDuration = null)
    {
        var required = requiredDuration ?? TimeSpan.FromMinutes(1);
        if (samples.Count == 0) return new(false, 0, TimeSpan.Zero);

        var ordered = samples.Where(s => s.AtUtc <= nowUtc).OrderBy(s => s.AtUtc).ToArray();
        if (ordered.Length == 0) return new(false, 0, TimeSpan.Zero);
        var latest = ordered[^1];
        if (latest.UsedPercent <= thresholdPercent) return new(false, latest.UsedPercent, TimeSpan.Zero);

        var start = latest.AtUtc;
        for (var i = ordered.Length - 2; i >= 0; i--)
        {
            if (ordered[i].UsedPercent <= thresholdPercent) break;
            start = ordered[i].AtUtc;
        }

        var sustained = latest.AtUtc - start;
        return new(sustained >= required, latest.UsedPercent, sustained);
    }
}
