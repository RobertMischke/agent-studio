namespace AgentStudio.Retention;

public enum ArtifactClass
{
    Authority,
    Evidence,
    HeavyWorkingData,
    Runtime,
}

public sealed record ArtifactRetentionRule(
    string Id,
    long? HotCapBytesPerFile,
    long? HotBudgetBytesPerTask,
    int? ArchiveAfterDaysTerminal,
    int? DeleteAfterDays,
    IReadOnlyList<string> NeverArchiveLanes,
    long? RefuseAboveBytes = null,
    int? WholeTaskAfterDaysTerminal = null,
    int? TombstoneAfterDaysTerminal = null,
    bool TombstoneDeletionEnabled = false,
    IReadOnlyDictionary<string, long>? HotCapBytesPerFamily = null,
    IReadOnlyDictionary<string, int>? DeleteAfterDaysPerFamily = null);

public sealed record RetentionPolicy(
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    IReadOnlyDictionary<ArtifactClass, ArtifactRetentionRule> WorkspaceDefaults,
    IReadOnlyDictionary<string, IReadOnlyDictionary<ArtifactClass, ArtifactRetentionRule>> ProjectOverrides)
{
    public const long MiB = 1024L * 1024L;

    private static IReadOnlyList<string> NeverArchive { get; } =
    [
        "0-inbox", "1-preparation", "2-ready", "3-progress", "4-auto-review",
        "5-human-review", "5e-escalated",
    ];

    public static RetentionPolicy Default { get; } = new(
        1,
        new DateTimeOffset(2026, 9, 6, 21, 0, 0, TimeSpan.Zero),
        "platform-default",
        new Dictionary<ArtifactClass, ArtifactRetentionRule>
        {
            [ArtifactClass.Authority] = new("authority-keep", null, null, null, null, NeverArchive),
            [ArtifactClass.Evidence] = new("task-stub-180d", null, null, 180, null, NeverArchive, WholeTaskAfterDaysTerminal: 180),
            [ArtifactClass.HeavyWorkingData] = new(
                "heavy-excerpt-30d", 25 * MiB, 64 * MiB, 30, null, NeverArchive,
                RefuseAboveBytes: 50 * MiB, WholeTaskAfterDaysTerminal: 180,
                TombstoneAfterDaysTerminal: 730, TombstoneDeletionEnabled: false,
                HotCapBytesPerFamily: new Dictionary<string, long>
                {
                    ["cli-output"] = 10 * MiB,
                    ["review-stdout"] = 10 * MiB,
                    ["results"] = 25 * MiB,
                    ["attachments"] = 25 * MiB,
                }),
            [ArtifactClass.Runtime] = new(
                "runtime-delete-30d", null, null, null, 30, Array.Empty<string>(),
                DeleteAfterDaysPerFamily: new Dictionary<string, int>
                {
                    ["bus"] = 30,
                    ["attempt-authority-archive"] = 90,
                    ["rotation"] = 7,
                }),
        },
        new Dictionary<string, IReadOnlyDictionary<ArtifactClass, ArtifactRetentionRule>>(StringComparer.OrdinalIgnoreCase));

    public ArtifactRetentionRule Resolve(string project, ArtifactClass artifactClass)
    {
        var projectRules = ProjectOverrides.TryGetValue(project, out var exact)
            ? exact
            : ProjectOverrides.FirstOrDefault(item => item.Key.Equals(project, StringComparison.OrdinalIgnoreCase)).Value;
        if (projectRules != null
            && projectRules.TryGetValue(artifactClass, out var rule))
            return rule;
        return WorkspaceDefaults[artifactClass];
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (artifactClass, rule) in WorkspaceDefaults)
        {
            if (rule.ArchiveAfterDaysTerminal is > 0 and < 7)
                errors.Add($"{artifactClass}: archiveAfterDaysTerminal must be at least 7.");
            if (rule.DeleteAfterDays is > 0 and < 7)
                errors.Add($"{artifactClass}: deleteAfterDays must be at least 7.");
            if (rule.HotCapBytesPerFile is { } cap && rule.HotBudgetBytesPerTask is { } budget && budget < cap)
                errors.Add($"{artifactClass}: hotBudgetBytesPerTask must be at least hotCapBytesPerFile.");
            if (rule.RefuseAboveBytes is > 95 * MiB)
                errors.Add($"{artifactClass}: refuseAboveBytes must not exceed 95 MiB.");
        }
        foreach (var (project, overrides) in ProjectOverrides)
        foreach (var (artifactClass, rule) in overrides)
        {
            var required = WorkspaceDefaults[artifactClass].NeverArchiveLanes;
            if (required.Except(rule.NeverArchiveLanes, StringComparer.OrdinalIgnoreCase).Any())
                errors.Add($"{project}/{artifactClass}: neverArchiveLanes cannot remove workspace exclusions.");
        }
        return errors;
    }
}
