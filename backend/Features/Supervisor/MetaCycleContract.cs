namespace AgentStudio.Supervisor;

/// <summary>
/// Per-project meta-cycle data shapes. The full design is in
/// <c>docs/mockups/orchestrator-meta-cycle/</c> and <c>ADR-0022</c>.
/// </summary>
/// <remarks>
/// The meta-cycle is the per-project loop above the runner that pauses after
/// N jobs reach <c>4-review</c>, inspects a fixed envelope of artefacts, and
/// picks one of four typed actions. It reuses the supervisor's
/// <c>PausePickup</c> / <c>Resume</c> primitives so the runner stays the
/// single state-machine authority. Off by default behind
/// <c>Supervisor:MetaCycleEnabled</c>.
/// </remarks>
public enum MetaCycleVerdict
{
    Healthy,
    FixTriggering,
    EscalationOnly,
    Aborted,
}

public enum MetaCycleActionKind
{
    Resume,
    UpdateStableThenResume,
    QueueFix,
    EscalateToUser,
    NoOp,
}

public sealed record MetaCycleAction(
    MetaCycleActionKind Kind,
    string Reason,
    string? FollowUpJobId = null,
    string? FollowUpState = null);

public sealed record MetaCycleJobObservation(
    string JobId,
    string Title,
    int NewCommits,
    bool HasArtefacts);

public sealed record MetaCycleCommitLogDiff(
    int TotalCommits,
    string? FromSha,
    string? ToSha);

public sealed record MetaCycleCrashMarker(
    bool Present,
    DateTime? At,
    string? Details);

public sealed record MetaCycleAdvisorySummary(
    int CountAtOrAboveThreshold,
    IReadOnlyList<string> Topics);

public sealed record MetaCycleStuckInProgress(
    int Count,
    IReadOnlyList<string> JobIds);

public sealed record MetaCycleExpectedArtefacts(
    int MissingCount,
    IReadOnlyList<string> JobIds);

public sealed record MetaCycleRunnerModeDrift(
    bool Drifted,
    string? Expected,
    string? Actual);

public sealed record MetaCycleInspection(
    MetaCycleCommitLogDiff CommitLogDiff,
    MetaCycleCrashMarker LastCrashMarker,
    MetaCycleAdvisorySummary SupervisorAdvisories,
    MetaCycleStuckInProgress StuckInProgress,
    MetaCycleExpectedArtefacts ExpectedArtefacts,
    MetaCycleRunnerModeDrift RunnerModeDrift,
    IReadOnlyDictionary<string, object>? Extras = null);

public sealed record MetaCycleFinding(
    string Topic,
    SupervisorSeverity Severity,
    string Message,
    string? JobId = null,
    IReadOnlyList<string>? Evidence = null);

/// <summary>
/// One report from the per-project meta-cycle. Append-only; one file per
/// cycle under <c>logs/meta/&lt;project&gt;/meta-cycle/&lt;timestamp&gt;.json</c>.
/// Field-level rules live in
/// <c>docs/app/schemas/meta-cycle-report.schema.json</c>; this record is the
/// in-process projection.
/// </summary>
public sealed record MetaCycleReport(
    string CycleId,
    string Project,
    DateTime StartedAt,
    DateTime CompletedAt,
    int CycleLengthN,
    IReadOnlyList<MetaCycleJobObservation> JobsObserved,
    MetaCycleInspection Inspection,
    IReadOnlyList<MetaCycleFinding> Findings,
    MetaCycleVerdict Verdict,
    MetaCycleAction Action,
    IReadOnlyDictionary<string, object>? ConfigSnapshot = null);

/// <summary>
/// Per-project knobs for the meta-cycle. Defaults come from
/// <c>Supervisor:MetaCycle*</c>; overrides may live in
/// <c>project-settings.json</c> under a <c>MetaCycle</c> block.
/// </summary>
public sealed record MetaCycleConfig(
    bool Enabled,
    int CycleLengthN,
    TimeSpan StuckInProgressThreshold,
    SupervisorSeverity AdvisorySeverityThreshold,
    bool RunUpdateStableOnHealthy,
    int MaxFixesPerHour,
    IReadOnlyList<string> ExtraGlobs,
    IReadOnlyList<string> ExtraAdvisoryTopics,
    string ExtraGlobAction)
{
    public static MetaCycleConfig Defaults() => new(
        Enabled: false,
        CycleLengthN: 2,
        StuckInProgressThreshold: TimeSpan.FromMinutes(30),
        AdvisorySeverityThreshold: SupervisorSeverity.Warn,
        RunUpdateStableOnHealthy: false,
        MaxFixesPerHour: 2,
        ExtraGlobs: Array.Empty<string>(),
        ExtraAdvisoryTopics: Array.Empty<string>(),
        ExtraGlobAction: "inform");

    public static MetaCycleConfig FromConfiguration(IConfiguration configuration)
    {
        var d = Defaults();
        var section = configuration.GetSection("Supervisor");
        return d with
        {
            Enabled = section.GetValue("MetaCycleEnabled", d.Enabled),
            CycleLengthN = section.GetValue("MetaCycleDefaultCycleLength", d.CycleLengthN),
            StuckInProgressThreshold = TimeSpan.FromMinutes(section.GetValue("MetaCycleDefaultStuckMinutes", (int)d.StuckInProgressThreshold.TotalMinutes)),
            AdvisorySeverityThreshold = ParseSeverity(section.GetValue<string?>("MetaCycleDefaultSeverity"), d.AdvisorySeverityThreshold),
            MaxFixesPerHour = section.GetValue("MetaCycleMaxFixesPerHour", d.MaxFixesPerHour),
        };
    }

    public static SupervisorSeverity ParseSeverity(string? raw, SupervisorSeverity fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return Enum.TryParse<SupervisorSeverity>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
