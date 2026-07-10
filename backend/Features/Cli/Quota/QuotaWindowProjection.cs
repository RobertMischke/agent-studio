using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Forward-looking burn-rate projection over a CLI's quota windows (AGT-2055,
/// requirement 6). A snapshot only tells us the <i>current</i> usage; the
/// operator wants the scheduler to see the wall coming - "aus Burn-Rate +
/// Restfenster PROJIZIEREN, ob das Budget vor dem Reset reisst" - so it can
/// switch models or throttle <b>before</b> a window is actually exhausted.
///
/// <para>
/// The math is a deliberately simple linear extrapolation from the window's
/// start: given the window length (inferred from its label), the moment it
/// resets (<see cref="QuotaWindow.ResetAt"/>) and the fraction already elapsed,
/// we extrapolate the current used-percent to the end of the window. There is
/// no per-CLI usage history to build a smarter rate from, and a linear "if this
/// pace holds" is exactly the intuition an operator reasons with. It is
/// intentionally conservative: it never fires in the first few percent of a
/// window (too little signal), and windows whose length cannot be inferred are
/// simply skipped rather than guessed.
/// </para>
/// </summary>
public static class QuotaWindowProjection
{
    /// <summary>
    /// Below this elapsed fraction we refuse to project: extrapolating from a
    /// sliver of a window produces wildly unstable numbers (5% of a 5-hour
    /// window is 15 minutes).
    /// </summary>
    private const double MinElapsedFraction = 0.05;

    /// <summary>
    /// Project one window to the end of its current period. Returns null when
    /// the window cannot be projected (no reset time, no inferable length, no
    /// usage, or too early in the window to be meaningful).
    /// </summary>
    public static QuotaProjection? Project(QuotaWindow window, DateTime nowUtc, int capPct)
    {
        if (window == null || window.UsedPct is null || window.ResetAt is null) return null;
        var length = InferWindowLength(window.Label);
        if (length is null) return null;

        var reset = window.ResetAt.Value;
        var start = reset - length.Value;
        var elapsed = nowUtc - start;
        if (elapsed <= TimeSpan.Zero) return null;                 // window not started yet (clock skew)
        if (elapsed > length.Value) elapsed = length.Value;         // stale snapshot past its reset

        var fraction = elapsed.TotalHours / length.Value.TotalHours;
        if (fraction < MinElapsedFraction) return null;             // too early to trust the slope

        var used = window.UsedPct.Value;
        var projected = used / fraction;                            // "if this pace holds to reset"
        var burnPerHour = used / elapsed.TotalHours;
        var hoursRemaining = Math.Max(0d, (reset - nowUtc).TotalHours);

        return new QuotaProjection(
            WindowLabel: window.Label,
            CurrentUsedPct: used,
            ProjectedUsedPct: projected,
            BurnRatePctPerHour: burnPerHour,
            HoursRemaining: hoursRemaining,
            CapPct: capPct,
            ResetAt: reset,
            // Only a window that is currently UNDER the cap but projected to
            // cross it is a "wall ahead". An already-over-cap window is handled
            // by the strict cap check, not the projection.
            BreachesBeforeReset: used < capPct && projected >= capPct);
    }

    /// <summary>
    /// Evaluate a whole snapshot for a projected (not-yet-breached) cap cross.
    /// Returns the most-overshooting projected window as a
    /// <see cref="CapEvaluation"/> with <see cref="CapEvaluation.Projected"/>
    /// set, or null when nothing is projected to breach.
    /// </summary>
    public static CapEvaluation? EvaluateProjectedBreach(
        QuotaSnapshot? snapshot, CliQuotaCapsService caps, DateTime nowUtc)
    {
        if (snapshot?.Windows == null || snapshot.Windows.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(snapshot.CliType)) return null;

        CapEvaluation? worst = null;
        double worstOvershoot = 0d;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label)) continue;
            var cap = caps.GetCap(snapshot.CliType, w.Label);
            var proj = Project(w, nowUtc, cap);
            if (proj is null || !proj.BreachesBeforeReset) continue;
            var overshoot = proj.ProjectedUsedPct - proj.CapPct;
            if (worst is null || overshoot > worstOvershoot)
            {
                worstOvershoot = overshoot;
                worst = new CapEvaluation(
                    Blocked: true,
                    CliType: snapshot.CliType,
                    WindowLabel: w.Label,
                    CapPct: cap,
                    UsedPct: w.UsedPct ?? 0d,
                    Projected: true,
                    ResetAt: w.ResetAt);
            }
        }
        return worst;
    }

    /// <summary>
    /// Return the worst (highest projected) window projection for a snapshot,
    /// for logging/event payloads. Null when nothing is projectable.
    /// </summary>
    public static QuotaProjection? WorstProjection(
        QuotaSnapshot? snapshot, CliQuotaCapsService caps, DateTime nowUtc)
    {
        if (snapshot?.Windows == null || string.IsNullOrWhiteSpace(snapshot.CliType)) return null;
        QuotaProjection? worst = null;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label)) continue;
            var proj = Project(w, nowUtc, caps.GetCap(snapshot.CliType, w.Label));
            if (proj is null) continue;
            if (worst is null || proj.ProjectedUsedPct > worst.ProjectedUsedPct) worst = proj;
        }
        return worst;
    }

    /// <summary>
    /// Infer the length of a quota window from its label. CLIs name their
    /// windows in human terms ("5-hour", "Weekly", "Monthly"); we map those to
    /// a duration so the elapsed fraction can be computed. Unknown shapes
    /// return null and are skipped by the projection (never guessed).
    /// </summary>
    public static TimeSpan? InferWindowLength(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var l = label.ToLowerInvariant();
        if (l.Contains("5-hour") || l.Contains("5 hour") || l.Contains("5h") || l.Contains("session"))
            return TimeSpan.FromHours(5);
        if (l.Contains("week") || l.Contains("7-day") || l.Contains("7 day") || l.Contains("7d"))
            return TimeSpan.FromDays(7);
        if (l.Contains("month"))
            return TimeSpan.FromDays(30);
        if (l.Contains("daily") || l.Contains("24-hour") || l.Contains("24 hour") || l.Contains("24h"))
            return TimeSpan.FromHours(24);
        if (l.Contains("hour"))
            return TimeSpan.FromHours(1);
        return null;
    }
}

/// <summary>
/// Numbers behind one window's projection, carried into the load-distribution
/// log/event so the operator sees the reasoning (burn rate, remaining budget,
/// remaining time) - "gute Transparenz dieses Themas Lastverteilung".
/// </summary>
public sealed record QuotaProjection(
    string WindowLabel,
    double CurrentUsedPct,
    double ProjectedUsedPct,
    double BurnRatePctPerHour,
    double HoursRemaining,
    int CapPct,
    DateTime? ResetAt,
    bool BreachesBeforeReset);
