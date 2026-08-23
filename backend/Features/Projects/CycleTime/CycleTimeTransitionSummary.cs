namespace AgentStudio.Projects;

public sealed record CycleTimeTransitionCell(string From, string To, int Count, string Direction);

public sealed record CycleTimeLaneDwell(
    string Lane,
    /// <summary>Number of stays that ended with a lane change.</summary>
    int Stays,
    double? P50Seconds,
    double? P90Seconds,
    double? MaxSeconds,
    double TotalSeconds);

public sealed record CycleTimeBounceCause(
    string Cause,
    string Label,
    int Count,
    int Tasks,
    /// <summary>Backward moves whose rework span is known (the task got back to the level it fell from).</summary>
    int ReworkKnown,
    double? ReworkP50Seconds,
    double? ReworkP90Seconds,
    double ReworkTotalSeconds,
    /// <summary>Most frequent cause details (quality-loop cause, integration outcome), most frequent first.</summary>
    IReadOnlyList<CycleTimeOutcomeCount> Details);

public sealed record CycleTimeLoopTask(
    string TaskId,
    string TaskKey,
    string Title,
    string WatchPath,
    int BackwardTransitions,
    double LeadTimeSeconds,
    /// <summary>Backward causes of this task, most frequent first.</summary>
    IReadOnlyList<CycleTimeOutcomeCount> Causes);

public sealed record CycleTimeTransitionSummary(
    int TotalTransitions,
    int BackwardTransitions,
    int TasksWithBackwardTransitions,
    /// <summary>Lanes that occur as source or target, in canonical order.</summary>
    IReadOnlyList<string> Lanes,
    IReadOnlyList<CycleTimeTransitionCell> Cells,
    IReadOnlyList<CycleTimeLaneDwell> LaneDwell,
    IReadOnlyList<CycleTimeBounceCause> BounceCauses,
    IReadOnlyList<CycleTimeLoopTask> TopLoops);

/// <summary>Pure aggregation of per-task transitions into the project matrix, dwell, cause, and loop views.</summary>
public static class CycleTimeTransitionAggregation
{
    public const int TopLoopCount = 8;

    public static CycleTimeTransitionSummary Build(IReadOnlyList<TaskCycleTime> rows)
    {
        var all = rows
            .SelectMany(row => (row.Transitions ?? []).Select(t => (Row: row, Transition: t)))
            .ToList();

        var lanes = all
            .SelectMany(x => new[] { x.Transition.From, x.Transition.To })
            .Where(lane => !string.IsNullOrWhiteSpace(lane))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(LaneOrder.CanonicalIndex)
            .ThenBy(lane => lane, StringComparer.Ordinal)
            .ToList();

        var cells = all
            .Where(x => !string.IsNullOrWhiteSpace(x.Transition.From) && !string.IsNullOrWhiteSpace(x.Transition.To))
            .GroupBy(x => (x.Transition.From, x.Transition.To))
            .Select(g => new CycleTimeTransitionCell(g.Key.From, g.Key.To, g.Count(), g.First().Transition.Direction))
            .OrderBy(c => LaneOrder.CanonicalIndex(c.From))
            .ThenBy(c => LaneOrder.CanonicalIndex(c.To))
            .ToList();

        var dwell = all
            .Where(x => x.Transition.DwellSeconds is not null && !string.IsNullOrWhiteSpace(x.Transition.From))
            .GroupBy(x => x.Transition.From)
            .Select(g =>
            {
                var values = g.Select(x => x.Transition.DwellSeconds!.Value).OrderBy(v => v).ToList();
                return new CycleTimeLaneDwell(
                    g.Key,
                    values.Count,
                    Round(CycleTimeStatistics.Median(values)),
                    Round(CycleTimeStatistics.Percentile(values, 0.90)),
                    Round(values[^1]),
                    Round(values.Sum()));
            })
            .OrderBy(d => LaneOrder.CanonicalIndex(d.Lane))
            .ToList();

        var backward = all.Where(x => x.Transition.Direction == TransitionDirections.Backward).ToList();
        var causes = backward
            .GroupBy(x => x.Transition.Cause, StringComparer.Ordinal)
            .Select(g =>
            {
                var rework = g.Where(x => x.Transition.ReworkSeconds is not null)
                    .Select(x => x.Transition.ReworkSeconds!.Value).OrderBy(v => v).ToList();
                var details = g
                    .Where(x => !string.IsNullOrWhiteSpace(x.Transition.CauseDetail))
                    .GroupBy(x => x.Transition.CauseDetail!, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new CycleTimeOutcomeCount(Trim(d.Key), d.Count()))
                    .OrderByDescending(d => d.Count)
                    .ThenBy(d => d.Outcome, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
                return new CycleTimeBounceCause(
                    g.Key,
                    TransitionCauses.Label(g.Key),
                    g.Count(),
                    g.Select(x => x.Row.TaskId).Distinct(StringComparer.Ordinal).Count(),
                    rework.Count,
                    rework.Count == 0 ? null : Round(CycleTimeStatistics.Median(rework)),
                    rework.Count == 0 ? null : Round(CycleTimeStatistics.Percentile(rework, 0.90)),
                    Round(rework.Sum()),
                    details);
            })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Cause, StringComparer.Ordinal)
            .ToList();

        var loops = rows
            .Where(row => row.BackwardTransitions > 0)
            .OrderByDescending(row => row.BackwardTransitions)
            .ThenByDescending(row => row.LeadTimeSeconds)
            .Take(TopLoopCount)
            .Select(row => new CycleTimeLoopTask(
                row.TaskId,
                row.TaskKey,
                row.Title,
                row.WatchPath,
                row.BackwardTransitions,
                row.LeadTimeSeconds,
                (row.Transitions ?? [])
                    .Where(t => t.Direction == TransitionDirections.Backward)
                    .GroupBy(t => t.Cause, StringComparer.Ordinal)
                    .Select(g => new CycleTimeOutcomeCount(g.Key, g.Count()))
                    .OrderByDescending(c => c.Count)
                    .ThenBy(c => c.Outcome, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

        return new CycleTimeTransitionSummary(
            all.Count,
            backward.Count,
            rows.Count(row => row.BackwardTransitions > 0),
            lanes,
            cells,
            dwell,
            causes,
            loops);
    }

    private static double Round(double value) => Math.Round(value, 1);

    private static string Trim(string value) =>
        value.Length <= 120 ? value : value[..117] + "...";
}
