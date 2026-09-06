namespace AgentStudio.Retention;

public sealed record ArchiveManifestFile(string RelativePath, long Size, string Sha256);

public sealed record ArchiveTransition(
    int Stage,
    DateTimeOffset At,
    string RuleId,
    string Actor);

public sealed record RetentionArchiveManifest
{
    public required string TaskKey { get; init; }
    public required string TaskId { get; init; }
    public required string Project { get; init; }
    public required string Lane { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    public required IReadOnlyList<string> RuleIds { get; init; }
    public required int PolicyVersion { get; init; }
    public required IReadOnlyList<ArchiveManifestFile> Files { get; init; }
    public required long TotalBytes { get; init; }
    public string Compression { get; init; } = "zip-deflate";
    public required string PayloadPath { get; init; }
    public required string PayloadSha256 { get; init; }
    public required DateTimeOffset ArchivedAt { get; init; }
    public required string ArchivedBy { get; init; }
    public IReadOnlyList<ArchiveTransition> Transitions { get; init; } = [];
    public DateTimeOffset? RestoredAt { get; init; }
}

public sealed record ArchiveManifestPointer
{
    public required DateTimeOffset ArchivedAt { get; init; }
    public required string ManifestPath { get; init; }
    public IReadOnlyList<string> ManifestPaths { get; init; } = [];
    public required long TotalBytes { get; init; }
    public required int FileCount { get; init; }
    public required int Stage { get; init; }
    public DateTimeOffset? RestoredAt { get; init; }
}

public sealed record ColdArchivePreparation(
    string ArchiveDirectory,
    string PayloadPath,
    string PayloadSha256,
    IReadOnlyList<ArchiveManifestFile> Files);

public interface IRetentionStore
{
    Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(
        string? project = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default);

    Task<Stream> ReadFileAsync(
        RetentionTaskInventory task,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<ColdArchivePreparation> MoveToColdAsync(
        RetentionTaskInventory task,
        IReadOnlyList<string> relativePaths,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default);

    Task<string> WriteManifestAsync(
        ColdArchivePreparation preparation,
        RetentionArchiveManifest manifest,
        CancellationToken cancellationToken = default);

    Task WriteStubAsync(
        RetentionTaskInventory task,
        IReadOnlyList<string> archivedRelativePaths,
        IReadOnlyList<RetentionExcerpt> excerpts,
        ArchiveManifestPointer pointer,
        CancellationToken cancellationToken = default);

    Task<RetentionArchiveManifest> RestoreAsync(
        string taskKey,
        string? project = null,
        CancellationToken cancellationToken = default);
}

public sealed record RetentionExecutionResult(
    int ArchivedTasks,
    int DeletedRuntimeFiles,
    long ArchivedBytes,
    long DeletedBytes,
    IReadOnlyList<string> ChangedProjects,
    IReadOnlyList<string> Errors);

public sealed class RetentionExecutor
{
    private readonly IRetentionStore _store;
    private readonly RetentionExcerptWriter _excerpts;

    public RetentionExecutor(IRetentionStore store, RetentionExcerptWriter? excerpts = null)
    {
        _store = store;
        _excerpts = excerpts ?? new RetentionExcerptWriter();
    }

    public async Task<RetentionExecutionResult> ApplyAsync(
        RetentionPlan plan,
        RetentionPolicy policy,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var inventory = await _store.EnumerateTasksAndFilesAsync(cancellationToken: cancellationToken);
        var tasks = inventory.ToDictionary(
            item => (item.Project.ToUpperInvariant(), item.Key.ToUpperInvariant()));
        var archivedTasks = 0;
        var deletedFiles = 0;
        long archivedBytes = 0;
        long deletedBytes = 0;
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var group in plan.Actions
                     .Where(action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask)
                     .GroupBy(action => (action.Project.ToUpperInvariant(), action.TaskKey!.ToUpperInvariant())))
        {
            if (!tasks.TryGetValue(group.Key, out var task))
                continue;
            var paths = group.SelectMany(action => action.RelativePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => File.Exists(Path.Combine(task.StorePath, path)))
                .ToArray();
            if (paths.Length == 0)
                continue;
            try
            {
                var stage = group.Any(action => action.Kind == RetentionActionKind.ArchiveTask) ? 2 : 1;
                var now = plan.PlannedAt;
                var excerpts = await _excerpts.CreateAsync(task.StorePath,
                    paths.Where(path => new ArtifactClassifier().Classify(path).ArtifactClass == ArtifactClass.HeavyWorkingData).ToArray(),
                    cancellationToken);
                var cold = await _store.MoveToColdAsync(task, paths, now, cancellationToken);
                var ruleIds = group.Select(action => action.RuleId).Distinct().ToArray();
                var manifest = new RetentionArchiveManifest
                {
                    TaskKey = task.Key,
                    TaskId = task.Id,
                    Project = task.Project,
                    Lane = task.Lane,
                    TerminalAt = task.TerminalAt,
                    RuleIds = ruleIds,
                    PolicyVersion = policy.Version,
                    Files = cold.Files,
                    TotalBytes = cold.Files.Sum(file => file.Size),
                    PayloadPath = cold.PayloadPath,
                    PayloadSha256 = cold.PayloadSha256,
                    ArchivedAt = now,
                    ArchivedBy = actor,
                    Transitions = [new ArchiveTransition(stage, now, string.Join(',', ruleIds), actor)],
                };
                var manifestPath = await _store.WriteManifestAsync(cold, manifest, cancellationToken);
                await _store.WriteStubAsync(task, paths, excerpts, new ArchiveManifestPointer
                {
                    ArchivedAt = now,
                    ManifestPath = manifestPath,
                    TotalBytes = manifest.TotalBytes,
                    FileCount = manifest.Files.Count,
                    Stage = stage,
                }, cancellationToken);
                archivedTasks++;
                archivedBytes += manifest.TotalBytes;
                projects.Add(task.Project);
            }
            catch (Exception exception)
            {
                errors.Add($"{task.Project}/{task.Key}: {exception.Message}");
            }
        }

        foreach (var action in plan.Actions.Where(action => action.Kind == RetentionActionKind.DeleteRuntime))
        {
            if (action.TaskKey is null
                || !tasks.TryGetValue((action.Project.ToUpperInvariant(), action.TaskKey.ToUpperInvariant()), out var task))
                continue;
            foreach (var path in action.RelativePaths)
            {
                var fullPath = Path.Combine(task.StorePath, path);
                if (!File.Exists(fullPath))
                    continue;
                var length = new FileInfo(fullPath).Length;
                File.Delete(fullPath);
                deletedFiles++;
                deletedBytes += length;
                projects.Add(task.Project);
            }
        }
        return new RetentionExecutionResult(
            archivedTasks, deletedFiles, archivedBytes, deletedBytes,
            projects.Order(StringComparer.OrdinalIgnoreCase).ToArray(), errors);
    }
}
