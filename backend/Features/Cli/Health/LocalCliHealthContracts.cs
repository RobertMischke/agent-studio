namespace AgentStudio.Cli;

public sealed record LocalCliCapabilityState(
    string CliType,
    bool Available,
    string? Version,
    string Path,
    string Classification,
    DateTimeOffset CheckedAt);

public sealed record NpmActivityEvidence(
    string Name,
    DateTimeOffset LastWriteAt,
    long LengthBytes,
    IReadOnlyList<string> RelevantLines);

internal sealed record LocalCliRepairEvent(
    string CliType,
    string PackageName,
    DateTimeOffset AttemptedAt,
    DateTimeOffset CompletedAt,
    string Stage,
    bool Succeeded,
    string Trigger,
    string? LastObservedVersionBefore,
    string? PackageVersionBefore,
    string? VersionAfter,
    string NpmBin,
    string PackagePath,
    DateTimeOffset? PackageManifestModifiedAt,
    IReadOnlyList<string> MissingShims,
    IReadOnlyList<NpmActivityEvidence> RecentNpmActivity,
    int? InstallerExitCode,
    string? InstallerOutputTail,
    string? InstallerErrorTail,
    string? Error);

public sealed record LocalCliRepairSummary(
    string CliType,
    string PackageName,
    DateTimeOffset AttemptedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    string Trigger,
    string? LastObservedVersionBefore,
    string? PackageVersionBefore,
    string? VersionAfter,
    string? Error);

public sealed record LocalCliHealthSnapshot(
    DateTimeOffset At,
    IReadOnlyList<LocalCliCapabilityState> Capabilities,
    LocalCliRepairSummary? LatestRepair);
