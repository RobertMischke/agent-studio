namespace AgentStudio.Projects;

/// <summary>
/// Aggregate of one stage, rollup, or count over the tasks in the window.
/// <see cref="Count"/> is the number of tasks that contribute a value: for
/// durations, tasks in which the stage occurred (seconds &gt; 0); for counts,
/// every task. Percentiles are therefore "when it happens" statistics.
/// </summary>
public sealed record CycleTimeStageAggregate(
    string Stage,
    string Label,
    /// <summary><c>stage</c> (additive lane stage), <c>rollup</c> (overlapping total), or <c>count</c>.</summary>
    string Kind,
    /// <summary><c>seconds</c> or <c>count</c>.</summary>
    string Unit,
    bool Highlighted,
    int Count,
    double? P50,
    double? P90,
    double? Max,
    double? Mean,
    double Total);

public sealed record CycleTimeOutcomeCount(string Outcome, int Count);

/// <summary>Percentile helpers shared by the cycle-time aggregation. Nearest-rank p90, classic median.</summary>
public static class CycleTimeStatistics
{
    public static CycleTimeStageAggregate Aggregate(
        string stage,
        string kind,
        string unit,
        IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return new CycleTimeStageAggregate(stage, CycleTimeStages.Label(stage), kind, unit,
                CycleTimeStages.Highlighted.Contains(stage), 0, null, null, null, null, 0);

        var total = sorted.Sum();
        return new CycleTimeStageAggregate(
            stage,
            CycleTimeStages.Label(stage),
            kind,
            unit,
            CycleTimeStages.Highlighted.Contains(stage),
            sorted.Count,
            Round(Median(sorted)),
            Round(Percentile(sorted, 0.90)),
            Round(sorted[^1]),
            Round(total / sorted.Count),
            Round(total));
    }

    /// <summary>Classic median: middle value, or the mean of the two middle values for an even count.</summary>
    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Nearest-rank percentile on an ascending list: value at ceil(p * n), 1-based.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double Round(double value) => Math.Round(value, 1);
}
