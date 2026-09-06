using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Retention;

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactClass>))]
public enum ArtifactClass
{
    Authority,
    Evidence,
    HeavyWorkingData,
    Runtime,
}

public sealed record ArtifactRetentionRule
{
    public required string Id { get; init; }
    public required ArtifactClass ArtifactClass { get; init; }
    public long? HotCapBytesPerFile { get; init; }
    public long? HotBudgetBytesPerTask { get; init; }
    public long? RefuseAboveBytes { get; init; }
    public IReadOnlyDictionary<string, long> HotCapBytesPerFileByFamily { get; init; }
        = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public int? ArchiveAfterDaysTerminal { get; init; }
    public int? DeleteAfterDays { get; init; }
    public IReadOnlyList<string> NeverArchiveLanes { get; init; } = [];
}

public sealed record RetentionRuleSet
{
    public required ArtifactRetentionRule Authority { get; init; }
    public required ArtifactRetentionRule Evidence { get; init; }
    public required ArtifactRetentionRule HeavyWorkingData { get; init; }
    public required ArtifactRetentionRule Runtime { get; init; }
    public int Stage1ExcerptAfterDaysTerminal { get; init; } = 30;
    public int Stage2StubAfterDaysTerminal { get; init; } = 180;
    public int? Stage3DeleteAfterDaysTerminal { get; init; }
    public bool CommitHeavyOnlyAtRunEnd { get; init; } = true;

    public ArtifactRetentionRule For(ArtifactClass artifactClass) => artifactClass switch
    {
        ArtifactClass.Authority => Authority,
        ArtifactClass.Evidence => Evidence,
        ArtifactClass.HeavyWorkingData => HeavyWorkingData,
        ArtifactClass.Runtime => Runtime,
        _ => throw new ArgumentOutOfRangeException(nameof(artifactClass)),
    };
}

public sealed record RetentionProjectOverride
{
    public int? Stage1ExcerptAfterDaysTerminal { get; init; }
    public int? Stage2StubAfterDaysTerminal { get; init; }
    public int? Stage3DeleteAfterDaysTerminal { get; init; }
    public long? HeavyHotCapBytesPerFile { get; init; }
    public long? HeavyHotBudgetBytesPerTask { get; init; }
    public int? RuntimeDeleteAfterDays { get; init; }
    public IReadOnlyList<string> NeverArchiveLanes { get; init; } = [];
}

public sealed record RetentionPolicy
{
    public int Version { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "platform-default";
    public required RetentionRuleSet WorkspaceDefaults { get; init; }
    public IReadOnlyDictionary<string, RetentionProjectOverride> ProjectOverrides { get; init; }
        = new Dictionary<string, RetentionProjectOverride>(StringComparer.OrdinalIgnoreCase);

    public static RetentionPolicy Default(DateTimeOffset? updatedAt = null) => new()
    {
        UpdatedAt = updatedAt ?? new DateTimeOffset(2026, 9, 6, 23, 0, 0, TimeSpan.FromHours(2)),
        WorkspaceDefaults = new RetentionRuleSet
        {
            Authority = new ArtifactRetentionRule
            {
                Id = "authority-keep",
                ArtifactClass = ArtifactClass.Authority,
                NeverArchiveLanes = DefaultNeverArchiveLanes,
            },
            Evidence = new ArtifactRetentionRule
            {
                Id = "evidence-stage2",
                ArtifactClass = ArtifactClass.Evidence,
                ArchiveAfterDaysTerminal = 180,
                NeverArchiveLanes = DefaultNeverArchiveLanes,
            },
            HeavyWorkingData = new ArtifactRetentionRule
            {
                Id = "heavy-stage1",
                ArtifactClass = ArtifactClass.HeavyWorkingData,
                HotCapBytesPerFile = 10 * MiB,
                HotCapBytesPerFileByFamily = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cli-output"] = 10 * MiB,
                    ["review-stdout"] = 10 * MiB,
                    ["results"] = 25 * MiB,
                    ["attachments"] = 25 * MiB,
                },
                HotBudgetBytesPerTask = 64 * MiB,
                RefuseAboveBytes = 50 * MiB,
                ArchiveAfterDaysTerminal = 30,
                NeverArchiveLanes = DefaultNeverArchiveLanes,
            },
            Runtime = new ArtifactRetentionRule
            {
                Id = "runtime-rotate",
                ArtifactClass = ArtifactClass.Runtime,
                DeleteAfterDays = 30,
                NeverArchiveLanes = DefaultNeverArchiveLanes,
            },
        },
    };

    public RetentionRuleSet Resolve(string project)
    {
        if (!ProjectOverrides.TryGetValue(project, out var value))
            return WorkspaceDefaults;

        var requiredLanes = WorkspaceDefaults.HeavyWorkingData.NeverArchiveLanes;
        var lanes = requiredLanes
            .Concat(value.NeverArchiveLanes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return WorkspaceDefaults with
        {
            Stage1ExcerptAfterDaysTerminal = value.Stage1ExcerptAfterDaysTerminal
                ?? WorkspaceDefaults.Stage1ExcerptAfterDaysTerminal,
            Stage2StubAfterDaysTerminal = value.Stage2StubAfterDaysTerminal
                ?? WorkspaceDefaults.Stage2StubAfterDaysTerminal,
            Stage3DeleteAfterDaysTerminal = value.Stage3DeleteAfterDaysTerminal
                ?? WorkspaceDefaults.Stage3DeleteAfterDaysTerminal,
            HeavyWorkingData = WorkspaceDefaults.HeavyWorkingData with
            {
                HotCapBytesPerFile = value.HeavyHotCapBytesPerFile
                    ?? WorkspaceDefaults.HeavyWorkingData.HotCapBytesPerFile,
                HotBudgetBytesPerTask = value.HeavyHotBudgetBytesPerTask
                    ?? WorkspaceDefaults.HeavyWorkingData.HotBudgetBytesPerTask,
                NeverArchiveLanes = lanes,
            },
            Evidence = WorkspaceDefaults.Evidence with { NeverArchiveLanes = lanes },
            Runtime = WorkspaceDefaults.Runtime with
            {
                DeleteAfterDays = value.RuntimeDeleteAfterDays
                    ?? WorkspaceDefaults.Runtime.DeleteAfterDays,
                NeverArchiveLanes = lanes,
            },
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        ValidateRules(WorkspaceDefaults, "workspaceDefaults", errors);
        foreach (var (project, _) in ProjectOverrides)
            ValidateRules(Resolve(project), $"projectOverrides.{project}", errors);
        return errors;
    }

    public static RetentionPolicy Load(string path)
    {
        var policy = JsonSerializer.Deserialize<RetentionPolicy>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Retention policy is empty.");
        var errors = policy.Validate();
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));
        return policy;
    }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void ValidateRules(RetentionRuleSet rules, string scope, ICollection<string> errors)
    {
        if (rules.Stage1ExcerptAfterDaysTerminal < 7)
            errors.Add($"{scope}.stage1ExcerptAfterDaysTerminal must be at least 7.");
        if (rules.Stage2StubAfterDaysTerminal < rules.Stage1ExcerptAfterDaysTerminal)
            errors.Add($"{scope}.stage2StubAfterDaysTerminal must not precede stage 1.");
        if (rules.Stage3DeleteAfterDaysTerminal is < 7)
            errors.Add($"{scope}.stage3DeleteAfterDaysTerminal must be at least 7 when enabled.");
        var heavy = rules.HeavyWorkingData;
        if (heavy.HotCapBytesPerFile is null or < 1)
            errors.Add($"{scope}.heavyWorkingData.hotCapBytesPerFile is required.");
        if (heavy.HotBudgetBytesPerTask < heavy.HotCapBytesPerFile)
            errors.Add($"{scope}.heavyWorkingData.hotBudgetBytesPerTask must cover the file cap.");
        if (heavy.RefuseAboveBytes is null or < 1 or > 95 * MiB)
            errors.Add($"{scope}.heavyWorkingData.refuseAboveBytes must be from 1 byte through 95 MiB.");
        if (rules.Runtime.DeleteAfterDays is < 7)
            errors.Add($"{scope}.runtime.deleteAfterDays must be at least 7.");
    }

    private const long MiB = 1024L * 1024L;
    private static readonly string[] DefaultNeverArchiveLanes =
    [
        "0-backlog", "1-preparation", "2-ready", "3-progress", "4-auto-review",
        "5-human-review", "5e-escalated",
    ];
}
