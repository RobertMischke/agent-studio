using System.Security.Cryptography;

namespace AgentStudio.Retention;

public sealed class RetentionExecutor
{
    private readonly IRetentionStore _store;
    private readonly RetentionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public RetentionExecutor(IRetentionStore store, RetentionPolicy policy, TimeProvider? timeProvider = null)
    {
        _store = store;
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RetentionRunResult> ApplyAsync(RetentionPlan plan, string actor, CancellationToken cancellationToken = default)
    {
        var inventory = await _store.EnumerateTasksAndFilesAsync(cancellationToken: cancellationToken);
        var byTask = inventory.ToDictionary(task => $"{task.Project}\n{task.TaskKey}", StringComparer.OrdinalIgnoreCase);
        var executed = 0;
        long archived = 0;
        long deleted = 0;

        foreach (var taskPlan in plan.Tasks)
        {
            if (!byTask.TryGetValue($"{taskPlan.Project}\n{taskPlan.TaskKey}", out var task)) continue;
            foreach (var action in taskPlan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (action.Kind == RetentionActionKind.RefuseOversize) continue;
                if (action.Kind == RetentionActionKind.DeleteRuntime)
                {
                    if (_store is FileTreeRetentionStore fileTree)
                        await fileTree.DeleteRuntimeAsync(task, action, cancellationToken);
                    deleted += action.Bytes;
                    executed++;
                    continue;
                }

                if (action.Kind == RetentionActionKind.ArchiveHeavy && _store is FileTreeRetentionStore excerptStore)
                    await WriteExcerptsAsync(excerptStore, task, action, cancellationToken);

                var archivedAt = _timeProvider.GetUtcNow();
                var files = new List<ArchiveManifestFile>();
                foreach (var path in action.RelativePaths)
                {
                    var file = task.Files.SingleOrDefault(item => item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (file == null || !File.Exists(Path.Combine(task.TaskDirectory, path))) continue;
                    await using var stream = await _store.ReadFileAsync(task, path, cancellationToken);
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                    files.Add(new ArchiveManifestFile(path, file.Size, Convert.ToHexString(hash).ToLowerInvariant()));
                }
                if (files.Count == 0) continue;
                var manifest = new ArchiveManifest(
                    task.TaskKey, task.TaskId, task.Project, task.Lane, task.TerminalAt, archivedAt,
                    [action.RuleId], _policy.Version, files, files.Sum(file => file.Size), "zip-deflate", string.Empty,
                    actor, action.Kind == RetentionActionKind.ArchiveHeavy ? "stage-1-excerpt" : "stage-2-stub");
                await _store.MoveToColdAsync(task, action, manifest, cancellationToken);
                if (_store is FileTreeRetentionStore pathStore)
                {
                    var manifestPath = pathStore.GetManifestPath(task, archivedAt);
                    var pointer = new ArchivePointer(
                        archivedAt, manifestPath, [manifestPath], manifest.TotalBytes, manifest.Files.Count);
                    await _store.WriteStubAsync(task, pointer, cancellationToken);
                }
                archived += manifest.TotalBytes;
                executed++;
            }
        }
        return new RetentionRunResult(plan, executed, archived, deleted);
    }

    private static async Task WriteExcerptsAsync(
        FileTreeRetentionStore store,
        RetentionTaskInventory task,
        RetentionAction action,
        CancellationToken cancellationToken)
    {
        var results = new List<(string Path, long Size, string Sha256)>();
        foreach (var path in action.RelativePaths)
        {
            var source = Path.Combine(task.TaskDirectory, path);
            if (!File.Exists(source)) continue;
            var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (ArtifactClassifier.Normalize(path).Contains("results/", StringComparison.Ordinal))
                results.Add((path, bytes.LongLength, hash));
            var excerpt = RetentionExcerptWriter.Write(path, bytes);
            var excerptPath = Path.Combine(task.TaskDirectory, ".retention-excerpts", SafeExcerptName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(excerptPath)!);
            await File.WriteAllTextAsync(excerptPath, excerpt, cancellationToken);
        }
        if (results.Count > 0)
        {
            var path = Path.Combine(task.TaskDirectory, ".retention-excerpts", "results-inventory.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, RetentionExcerptWriter.WriteResultsInventory(results), cancellationToken);
        }
    }

    private static string SafeExcerptName(string path)
    {
        var value = path.Replace('\\', '-').Replace('/', '-');
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value + ".md";
    }
}
