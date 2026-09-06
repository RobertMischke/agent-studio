namespace AgentStudio.Retention;

public interface IRetentionStore
{
    Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(
        string? project = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default);

    Task<Stream> ReadFileAsync(RetentionTaskInventory task, string relativePath, CancellationToken cancellationToken = default);
    Task WriteManifestAsync(RetentionTaskInventory task, ArchiveManifest manifest, CancellationToken cancellationToken = default);
    Task MoveToColdAsync(RetentionTaskInventory task, RetentionAction action, ArchiveManifest manifest, CancellationToken cancellationToken = default);
    Task WriteStubAsync(RetentionTaskInventory task, ArchivePointer pointer, CancellationToken cancellationToken = default);
    Task<RestoreResult> RestoreAsync(string taskKey, CancellationToken cancellationToken = default);
}

public sealed record ArchiveManifestFile(string RelativePath, long Size, string Sha256);

public sealed record ArchiveManifest(
    string TaskKey,
    string TaskId,
    string Project,
    string Lane,
    DateTimeOffset? TerminalAt,
    DateTimeOffset ArchivedAt,
    IReadOnlyList<string> RuleIds,
    int PolicyVersion,
    IReadOnlyList<ArchiveManifestFile> Files,
    long TotalBytes,
    string Compression,
    string PayloadSha256,
    string Actor,
    string Stage,
    DateTimeOffset? RestoredAt = null);

public sealed record ArchivePointer(
    DateTimeOffset ArchivedAt,
    string ManifestPath,
    IReadOnlyList<string> ManifestPaths,
    long TotalBytes,
    int FileCount,
    DateTimeOffset? RestoredAt = null);

public sealed record RestoreResult(string TaskKey, int RestoredFiles, long RestoredBytes, DateTimeOffset RestoredAt);

public sealed record RetentionRunResult(RetentionPlan Plan, int ExecutedActions, long ArchivedBytes, long DeletedBytes);
