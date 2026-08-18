namespace AgentStudio.HostHealth;

/// <summary>Whether an automatic repair may run now, and when to look again if not.</summary>
public sealed record LocalCliRepairThrottleDecision(bool Allowed, TimeSpan RetryAfter, string Reason);

/// <summary>
/// Rate limit for the one side effect this feature owns. A global npm install
/// costs minutes and hundreds of megabytes; a probe loop that hits a
/// permanently broken host must not turn into a reinstall loop. One automatic
/// attempt per CLI per window (default one hour) is enough to heal the
/// observed auto-update breakage, which happened twice in six days.
///
/// <para>
/// An operator-requested repair bypasses the window on purpose: the human is
/// the rate limit, and being told "try again in 47 minutes" after clicking
/// Repair is the kind of dead end the visibility half of this card exists to
/// remove.
/// </para>
/// </summary>
public static class LocalCliRepairThrottle
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    public static LocalCliRepairThrottleDecision Decide(
        DateTime? lastAttemptUtc,
        DateTime nowUtc,
        TimeSpan window,
        bool operatorRequested = false)
    {
        if (operatorRequested)
        {
            return new(true, TimeSpan.Zero, "operator-requested repair bypasses the automatic-attempt window");
        }

        if (lastAttemptUtc is null)
        {
            return new(true, TimeSpan.Zero, "no automatic repair attempted yet");
        }

        if (window <= TimeSpan.Zero)
        {
            return new(true, TimeSpan.Zero, "repair window is disabled");
        }

        var elapsed = nowUtc - lastAttemptUtc.Value;

        // A last-attempt stamp in the future means the clock moved backwards
        // (host time sync, suspend/resume). Treat it as "just attempted" and
        // wait out a full window rather than reinstalling on every tick.
        if (elapsed < TimeSpan.Zero)
        {
            return new(false, window,
                $"last automatic repair is stamped in the future; waiting out a full {FormatWindow(window)}");
        }

        if (elapsed >= window)
        {
            return new(true, TimeSpan.Zero,
                $"last automatic repair was {FormatWindow(elapsed)} ago (window {FormatWindow(window)})");
        }

        var retryAfter = window - elapsed;
        return new(false, retryAfter,
            $"an automatic repair already ran {FormatWindow(elapsed)} ago; next attempt in {FormatWindow(retryAfter)}");
    }

    private static string FormatWindow(TimeSpan value)
        => value.TotalMinutes < 1
            ? $"{Math.Max(0, (int)value.TotalSeconds)}s"
            : $"{(int)value.TotalMinutes}m";
}
