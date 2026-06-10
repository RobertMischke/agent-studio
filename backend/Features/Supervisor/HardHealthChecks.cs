namespace AgentStudio.Supervisor;

/// <summary>
/// Pure check predicates run against a <see cref="SupervisorObservation"/> on
/// each tick. Each check returns either no advisory (everything fine) or a
/// single advisory to append. Kept static so the rules are unit-testable
/// without a hosted service or file I/O.
/// </summary>
public static class HardHealthChecks
{
    /// <summary>
    /// No log line in the configured threshold while the runner reports a
    /// job is active. Either the agent is wedged or the log writer is.
    /// </summary>
    public static SupervisorAdvisory? NoProgress(SupervisorObservation o, TimeSpan threshold)
    {
        if (o.CurrentJobId == null) return null;
        if (o.LastProgressAt == null) return null;
        var age = o.CapturedAt - o.LastProgressAt.Value;
        if (age <= threshold) return null;
        return new SupervisorAdvisory(
            CreatedAt: o.CapturedAt,
            Project: o.Project,
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: $"No log line for {(int)age.TotalSeconds}s on active job (threshold {(int)threshold.TotalSeconds}s).",
            JobId: o.CurrentJobId);
    }

    /// <summary>
    /// Burst of error lines in the lookback window. The observation already
    /// pre-counted; we just decide if the count crosses the threshold.
    /// </summary>
    public static SupervisorAdvisory? ErrorBurst(SupervisorObservation o, int threshold)
    {
        var total = o.ErrorCounts.CliErrorsLastHour + o.ErrorCounts.OrchestratorErrorsLastHour;
        if (total < threshold) return null;
        return new SupervisorAdvisory(
            CreatedAt: o.CapturedAt,
            Project: o.Project,
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "error-burst",
            Message: $"{total} error-like lines in the last hour (threshold {threshold}). Run failures: {o.ErrorCounts.RunFailuresLastHour}.",
            JobId: o.CurrentJobId);
    }

    /// <summary>
    /// Quota window remaining is below the configured fraction.
    /// </summary>
    public static SupervisorAdvisory? QuotaCritical(SupervisorObservation o, double criticalUsedFraction)
    {
        if (o.Quota == null) return null;
        if (o.Quota.UsedFraction < criticalUsedFraction) return null;
        return new SupervisorAdvisory(
            CreatedAt: o.CapturedAt,
            Project: o.Project,
            Severity: SupervisorSeverity.High,
            Source: SupervisorSource.HardCheck,
            Topic: "quota-critical",
            Message: $"{o.Quota.Cli} quota at {(o.Quota.UsedFraction * 100):0.0}% used (threshold {(criticalUsedFraction * 100):0}%). Reset {o.Quota.ResetAt?.ToString("u") ?? "unknown"}.",
            JobId: o.CurrentJobId);
    }

    /// <summary>
    /// Tool-call repetition: the same agent sample appears more than the
    /// allowed count in the recent window. Cheap heuristic for a stuck
    /// inner loop.
    /// </summary>
    public static SupervisorAdvisory? ToolCallRepeat(SupervisorObservation o, int maxRepeat)
    {
        if (o.RecentAgentSamples.Count == 0) return null;
        var groups = o.RecentAgentSamples
            .GroupBy(s => s, StringComparer.Ordinal)
            .Where(g => g.Count() > maxRepeat)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (groups == null) return null;
        var sample = groups.Key.Length > 80 ? groups.Key[..80] + "..." : groups.Key;
        return new SupervisorAdvisory(
            CreatedAt: o.CapturedAt,
            Project: o.Project,
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "tool-call-repeat",
            Message: $"\"{sample}\" appeared {groups.Count()} times in the last {o.RecentAgentSamples.Count} samples (limit {maxRepeat}).",
            JobId: o.CurrentJobId);
    }

    public static IEnumerable<SupervisorAdvisory> RunAll(SupervisorObservation o, HardCheckThresholds t)
    {
        var a = NoProgress(o, t.NoProgressThreshold);          if (a != null) yield return a;
        var b = ErrorBurst(o, t.ErrorBurstThreshold);          if (b != null) yield return b;
        var c = QuotaCritical(o, t.QuotaCriticalFraction);     if (c != null) yield return c;
        var d = ToolCallRepeat(o, t.ToolCallRepeatLimit);      if (d != null) yield return d;
    }
}

/// <summary>
/// Tunable thresholds for <see cref="HardHealthChecks"/>. Loaded from
/// configuration with sensible defaults; per-project overrides are a
/// follow-up.
/// </summary>
public sealed record HardCheckThresholds(
    TimeSpan NoProgressThreshold,
    int ErrorBurstThreshold,
    double QuotaCriticalFraction,
    int ToolCallRepeatLimit)
{
    public static HardCheckThresholds Defaults() => new(
        NoProgressThreshold: TimeSpan.FromMinutes(10),
        ErrorBurstThreshold: 5,
        QuotaCriticalFraction: 0.95,
        ToolCallRepeatLimit: 5);

    public static HardCheckThresholds FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Supervisor");
        var d = Defaults();
        return new HardCheckThresholds(
            NoProgressThreshold: TimeSpan.FromSeconds(section.GetValue("NoProgressThresholdSeconds", (int)d.NoProgressThreshold.TotalSeconds)),
            ErrorBurstThreshold: section.GetValue("ErrorBurstThreshold", d.ErrorBurstThreshold),
            QuotaCriticalFraction: section.GetValue("QuotaCriticalFraction", d.QuotaCriticalFraction),
            ToolCallRepeatLimit: section.GetValue("ToolCallRepeatLimit", d.ToolCallRepeatLimit));
    }
}
