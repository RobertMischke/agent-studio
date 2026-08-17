using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplayRunner;

/// <summary>One planned emission: when it is due relative to the cycle start, and what to send.</summary>
public sealed record ReplayStep(TimeSpan Delay, DemoReplayEventRequest Request);

/// <summary>
/// Pure planner for one replay cycle. Given a signed trace and an epoch it
/// produces the full ordered emission plan, so the deterministic scene is a
/// property of data rather than of the loop that sends it.
/// </summary>
public static class ReplayCycle
{
    public static IReadOnlyList<ReplayStep> Plan(
        DemoReplaySignedTrace signed,
        long epoch,
        double speedFactor,
        DateTime cycleStartUtc)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (speedFactor <= 0) throw new ArgumentOutOfRangeException(nameof(speedFactor));

        var seals = signed.Seals.ToDictionary(seal => seal.Sequence, seal => seal.Signature);
        return signed.Trace.Frames
            .OrderBy(frame => frame.Sequence)
            .Select(frame => new ReplayStep(
                TimeSpan.FromSeconds(frame.OffsetSeconds / speedFactor),
                new DemoReplayEventRequest(
                    signed.Trace.TraceId,
                    signed.Digest,
                    epoch,
                    seals.TryGetValue(frame.Sequence, out var signature) ? signature : "",
                    frame,
                    cycleStartUtc.AddSeconds(frame.OffsetSeconds))))
            .ToList();
    }

    /// <summary>Wall-clock length of one cycle at the configured speed.</summary>
    public static TimeSpan Duration(DemoReplaySignedTrace signed, double speedFactor)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (speedFactor <= 0) throw new ArgumentOutOfRangeException(nameof(speedFactor));
        var last = signed.Trace.Frames.Count == 0 ? 0 : signed.Trace.Frames.Max(frame => frame.OffsetSeconds);
        return TimeSpan.FromSeconds(last / speedFactor);
    }
}
