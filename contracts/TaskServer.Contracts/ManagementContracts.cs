namespace AgentStudio.TaskServer.Contracts;

public enum TaskServerMode
{
    Normal,
    Draining,
    ReadOnly,
    Maintenance,
}

public sealed record TaskServerStatusDto(
    string ServerId,
    string ServerVersion,
    int SchemaVersion,
    TaskServerMode Mode,
    bool AuthorityReady,
    string DataDirectory,
    ProtocolRangeDto Protocol,
    DateTime StartedAt);

public sealed record ChangeModeRequest(TaskServerMode Mode, string Reason);
public sealed record PrepareShutdownRequest(string Reason);
public sealed record PrepareShutdownResult(bool SafeToStop, int UnresolvedAttempts, TaskServerMode Mode, string Message);
public sealed record BackupRequest(string? Name = null);
public sealed record BackupResult(string BackupId, string Path, string Sha256, DateTime CreatedAt, long SizeBytes);
public sealed record RestoreRequest(string BackupId, bool VerifyOnly = false);
public sealed record RestoreResult(string BackupId, bool Verified, bool Restored, string Sha256, string Message);
public sealed record ResolveUnknownAttemptRequest(string ContainmentProof, string Resolution = "requeue");

public sealed record LegacyMigrationRequest(
    string LegacyRoot,
    string WorkspaceName,
    bool FreezeConfirmed,
    bool PreserveEvidenceGit = true,
    string? ExpectedMigrationId = null);

public sealed record LegacyMigrationInventory(
    string MigrationId,
    string LegacyRoot,
    int Projects,
    int Tasks,
    int Events,
    int Artifacts,
    IReadOnlyList<string> EvidenceGitRoots,
    IReadOnlyList<string> Warnings);

public sealed record LegacyMigrationResult(
    string MigrationId,
    bool Imported,
    int Projects,
    int Tasks,
    int Events,
    int Artifacts,
    string IntegritySha256,
    string RollbackBoundary,
    IReadOnlyList<string> EvidenceGitRoots);
