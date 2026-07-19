namespace AgentStudio.Cli;

/// <summary>
/// Pure plausibility checks over successive quota snapshots. Guards against the
/// AGT-2064 class of glitch, where a single probe briefly reports a CLI as far
/// less used than it really is (e.g. an exhausted 5-hour window read as ~4%
/// used). Acting on such a value is dangerous: the operator, and the
/// quota-aware admission gate, would launch onto a CLI that is really at its
/// limit and take a launch-fail wave.
///
/// The rule the runner enforces: any Weekly-window decrease, or another window
/// that jumps DOWN by more than <see cref="DefaultDropThresholdPoints"/> points,
/// is suspicious when the previously announced reset has not passed. A
/// suspicious reading is not trusted until a second,
/// independent probe agrees with it (see <see cref="AreConsistent"/>); only
/// then does the drop replace the old value.
/// </summary>
public static class QuotaPlausibilityGate
{
    /// <summary>A downward jump larger than this (in percentage points) is suspicious unless a reset explains it.</summary>
    public const int DefaultDropThresholdPoints = 50;

    /// <summary>Two probes are "consistent" when every shared window agrees within this many points.</summary>
    public const double DefaultConsistencyTolerancePoints = 10;

    /// <summary>
    /// Decide whether <paramref name="candidate"/> is an implausible downward
    /// jump versus the last trusted <paramref name="previous"/> snapshot.
    /// Weekly windows are monotonic within one reset cycle, so every decrease
    /// is checked; other windows retain the broad spike threshold.
    /// </summary>
    public static SuspicionResult Evaluate(
        QuotaSnapshot? previous,
        QuotaSnapshot? candidate,
        DateTime nowUtc,
        int dropThresholdPoints = DefaultDropThresholdPoints)
    {
        if (previous?.Windows == null || candidate?.Windows == null) return SuspicionResult.Trusted;
        if (previous.Windows.Count == 0 || candidate.Windows.Count == 0) return SuspicionResult.Trusted;

        foreach (var prev in previous.Windows)
        {
            if (prev.UsedPct is not double prevUsed) continue;
            var cand = FindWindow(candidate.Windows, prev.Label);
            if (cand?.UsedPct is not double candUsed) continue;

            var drop = prevUsed - candUsed;
            var isWeekly = IsWeeklyWindow(prev.Label);
            if (drop <= 0 || (!isWeekly && drop <= dropThresholdPoints)) continue;
            if (ResetExplainsDrop(prev, cand, nowUtc, requireElapsedBoundary: isWeekly)) continue;

            return new SuspicionResult(
                true,
                $"{prev.Label} dropped {drop:0.#} points ({prevUsed:0.#}% -> {candUsed:0.#}%) with no reset to explain it");
        }

        return SuspicionResult.Trusted;
    }

    /// <summary>
    /// True when two probes taken close together report the same picture:
    /// every window present in both agrees within
    /// <paramref name="tolerancePoints"/>. Requires at least one shared window
    /// so "two empty probes" never counts as a confirmation. This is the
    /// "two consistent measurements" test that lets a real drop through.
    /// </summary>
    public static bool AreConsistent(
        QuotaSnapshot? a,
        QuotaSnapshot? b,
        double tolerancePoints = DefaultConsistencyTolerancePoints)
    {
        if (a?.Windows == null || b?.Windows == null) return false;

        var shared = 0;
        foreach (var wa in a.Windows)
        {
            if (wa.UsedPct is not double ua) continue;
            var wb = FindWindow(b.Windows, wa.Label);
            if (wb?.UsedPct is not double ub) continue;
            shared++;
            if (Math.Abs(ua - ub) > tolerancePoints) return false;
        }
        return shared > 0;
    }

    /// <summary>
    /// A downward jump is legitimate when the previously announced reset has
    /// passed. For non-Weekly windows, a later candidate boundary also retains
    /// the legacy allowance for short-window probe timing jitter.
    /// </summary>
    private static bool ResetExplainsDrop(
        QuotaWindow prev,
        QuotaWindow cand,
        DateTime nowUtc,
        bool requireElapsedBoundary)
    {
        if (prev.ResetAt is DateTime prevReset)
        {
            if (prevReset <= nowUtc) return true;
            // A Weekly candidate must not legitimise its own decrease merely by
            // advertising a later boundary. Until the previous boundary passes,
            // the cumulative weekly percentage is monotonic. Short windows keep
            // the legacy boundary-advance allowance for probe timing jitter.
            if (!requireElapsedBoundary
                && cand.ResetAt is DateTime candReset
                && candReset > prevReset) return true;
        }
        return false;
    }

    private static bool IsWeeklyWindow(string? label)
        => label?.Contains("Weekly", StringComparison.OrdinalIgnoreCase) == true;

    private static QuotaWindow? FindWindow(IEnumerable<QuotaWindow> windows, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        foreach (var w in windows)
        {
            if (string.Equals(w.Label?.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase))
                return w;
        }
        return null;
    }
}

/// <summary>Outcome of a plausibility check. <see cref="Reason"/> is set only when suspicious.</summary>
public readonly record struct SuspicionResult(bool Suspicious, string? Reason)
{
    public static readonly SuspicionResult Trusted = new(false, null);
}
