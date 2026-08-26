namespace AgentStudio.Cli;

public sealed record LocalCliCapabilityReport(
    DateTime At,
    IReadOnlyList<LocalCliCapability> Capabilities,
    LocalCliRepairReceipt? LatestRepair);

public sealed record LocalCliCapability(
    string CliType,
    string State,
    bool Available,
    string? CliVersion,
    string? PackageVersion,
    string ExecutablePath);

public sealed record LocalCliRepairReceipt(
    DateTime At,
    string CliType,
    string Outcome,
    string? CliVersionBefore,
    string? PackageVersionBefore,
    string? CliVersionAfter,
    string? PackageVersionAfter,
    string? Error);

public sealed record LocalNpmInstallationSnapshot(
    string Prefix,
    string PackageDirectory,
    bool PackagePresent,
    bool CallableShimPresent,
    string? PackageVersion,
    IReadOnlyList<string> RecentActivity);

public sealed record LocalNpmInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StdoutTail,
    string StderrTail,
    string? Error);

public sealed record LocalCliRepairJournalRecord
{
    public DateTime At { get; init; }
    public string CliType { get; init; } = "";
    public string NpmPackage { get; init; } = "";
    public string Detection { get; init; } = "missing-shim-with-package-present";
    public string Outcome { get; init; } = "";
    public string Command { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string NpmPrefix { get; init; } = "";
    public string PackageDirectory { get; init; } = "";
    public string? CliVersionBefore { get; init; }
    public string? PackageVersionBefore { get; init; }
    public string? CliVersionAfter { get; init; }
    public string? PackageVersionAfter { get; init; }
    public IReadOnlyList<string> ActivityBefore { get; init; } = [];
    public IReadOnlyList<string> ActivityAfter { get; init; } = [];
    public int? NpmExitCode { get; init; }
    public string? NpmStdoutTail { get; init; }
    public string? NpmStderrTail { get; init; }
    public string? Error { get; init; }

    public LocalCliRepairReceipt ToReceipt() => new(
        At,
        CliType,
        Outcome,
        CliVersionBefore,
        PackageVersionBefore,
        CliVersionAfter,
        PackageVersionAfter,
        Error);
}
