using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed class FileTreeRetentionStore : IRetentionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public FileTreeRetentionStore(string workspacePath, string? archivePath = null, TimeProvider? timeProvider = null)
    {
        WorkspacePath = Path.GetFullPath(workspacePath);
        ArchivePath = Path.GetFullPath(archivePath ?? DefaultArchivePath(WorkspacePath));
        TimeProvider = timeProvider ?? TimeProvider.System;
        if (!Directory.Exists(WorkspacePath))
            throw new DirectoryNotFoundException($"Workspace does not exist: {WorkspacePath}");
        var gitRoot = FindGitRoot(WorkspacePath);
        if (IsBelow(ArchivePath, gitRoot ?? WorkspacePath))
            throw new ArgumentException("The archive path must be outside the workspace repository.", nameof(archivePath));
    }

    public string WorkspacePath { get; }
    public string ArchivePath { get; }
    public TimeProvider TimeProvider { get; }

    public static string DefaultArchivePath(string workspacePath)
    {
        var full = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(full)!, "agent-taskboard-archive");
    }

    public async Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(
        string? project = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RetentionTaskInventory>();
        var projectsRoot = Path.Combine(WorkspacePath, "projects");
        if (Directory.Exists(projectsRoot))
        {
            foreach (var projectDirectory in Directory.EnumerateDirectories(projectsRoot).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectName = Path.GetFileName(projectDirectory);
                if (!string.IsNullOrWhiteSpace(project)
                    && !projectName.Equals(project, StringComparison.OrdinalIgnoreCase)) continue;
                var tasksRoot = Path.Combine(projectDirectory, "tasks");
                if (!Directory.Exists(tasksRoot)) continue;
                foreach (var taskJson in Directory.EnumerateFiles(tasksRoot, "task.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var taskDirectory = Path.GetDirectoryName(taskJson)!;
                    var item = await ReadTaskAsync(projectName, taskDirectory, taskJson, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(taskKey)
                        && !item.TaskKey.Equals(taskKey, StringComparison.OrdinalIgnoreCase)
                        && !item.TaskId.Equals(taskKey, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(item);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(taskKey) && string.IsNullOrWhiteSpace(project))
        {
            var runtimeFiles = EnumerateWorkspaceRuntimeFiles();
            if (runtimeFiles.Count > 0)
                result.Add(new RetentionTaskInventory(
                    "__workspace-runtime__", "__workspace-runtime__", "__workspace__", "runtime", null,
                    WorkspacePath, runtimeFiles));
        }
        return result;
    }

    public Task<Stream> ReadFileAsync(
        RetentionTaskInventory task,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveWithin(task.TaskDirectory, relativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public async Task WriteManifestAsync(
        RetentionTaskInventory task,
        ArchiveManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var path = GetManifestPath(task, manifest.ArchivedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteJsonAtomicAsync(path, manifest, cancellationToken);
    }

    public async Task MoveToColdAsync(
        RetentionTaskInventory task,
        RetentionAction action,
        ArchiveManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(task, manifest.ArchivedAt);
        var archiveDirectory = Path.GetDirectoryName(manifestPath)!;
        var payloadPath = Path.Combine(archiveDirectory, "payload.zip");
        if (File.Exists(manifestPath) && File.Exists(payloadPath))
        {
            var existing = await ReadJsonAsync<ArchiveManifest>(manifestPath, cancellationToken)
                           ?? throw new InvalidDataException($"Archive manifest is invalid: {manifestPath}");
            if (!existing.PayloadSha256.Equals(await HashFileAsync(payloadPath, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Existing archive payload hash mismatch: {payloadPath}");
            DeleteHotFiles(task, existing.Files);
            return;
        }

        Directory.CreateDirectory(archiveDirectory);
        var temporaryPayload = payloadPath + ".tmp";
        try
        {
            await using (var output = new FileStream(temporaryPayload, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = ResolveWithin(task.TaskDirectory, file.RelativePath);
                    if (!File.Exists(source)) continue;
                    var entry = zip.CreateEntry(file.RelativePath.Replace('\\', '/'), CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    await input.CopyToAsync(target, cancellationToken);
                }
            }
            File.Move(temporaryPayload, payloadPath, overwrite: true);
            var payloadHash = await HashFileAsync(payloadPath, cancellationToken);
            var finalManifest = manifest with { PayloadSha256 = payloadHash };
            await WriteJsonAtomicAsync(manifestPath, finalManifest, cancellationToken);

            DeleteHotFiles(task, manifest.Files);
        }
        finally
        {
            if (File.Exists(temporaryPayload)) File.Delete(temporaryPayload);
        }
    }

    public async Task WriteStubAsync(
        RetentionTaskInventory task,
        ArchivePointer pointer,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(task.TaskDirectory, "archive-manifest.json");
        if (File.Exists(path))
        {
            var current = await ReadJsonAsync<ArchivePointer>(path, cancellationToken);
            if (current != null)
            {
                var paths = current.ManifestPaths.Concat(pointer.ManifestPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                pointer = pointer with
                {
                    ManifestPaths = paths,
                    TotalBytes = current.TotalBytes + pointer.TotalBytes,
                    FileCount = current.FileCount + pointer.FileCount,
                };
            }
        }
        await WriteJsonAtomicAsync(path, pointer, cancellationToken);
    }

    public async Task DeleteRuntimeAsync(
        RetentionTaskInventory task,
        RetentionAction action,
        CancellationToken cancellationToken = default)
    {
        foreach (var relativePath in action.RelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveWithin(task.TaskDirectory, relativePath);
            if (File.Exists(path)) File.Delete(path);
        }
        await Task.CompletedTask;
    }

    public async Task<RestoreResult> RestoreAsync(string taskKey, CancellationToken cancellationToken = default)
        => await RestoreAsync(taskKey, null, cancellationToken);

    public async Task<RestoreResult> RestoreAsync(
        string taskKey,
        string? project,
        CancellationToken cancellationToken = default)
    {
        var tasks = await EnumerateTasksAndFilesAsync(project, taskKey, cancellationToken);
        var task = tasks.SingleOrDefault() ?? throw new InvalidOperationException($"Task not found: {taskKey}");
        var pointerPath = Path.Combine(task.TaskDirectory, "archive-manifest.json");
        var pointer = await ReadJsonAsync<ArchivePointer>(pointerPath, cancellationToken)
                      ?? throw new InvalidOperationException($"Task is not archived: {taskKey}");

        var restoredFiles = 0;
        long restoredBytes = 0;
        foreach (var manifestPathValue in pointer.ManifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.IsPathRooted(manifestPathValue)
                ? manifestPathValue
                : Path.GetFullPath(Path.Combine(WorkspacePath, manifestPathValue));
            var manifest = await ReadJsonAsync<ArchiveManifest>(manifestPath, cancellationToken)
                           ?? throw new InvalidDataException($"Archive manifest is missing: {manifestPath}");
            var payloadPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "payload.zip");
            var payloadHash = await HashFileAsync(payloadPath, cancellationToken);
            if (!payloadHash.Equals(manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Payload hash mismatch for {manifestPath}");

            using var zip = ZipFile.OpenRead(payloadPath);
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry(file.RelativePath.Replace('\\', '/'))
                            ?? throw new InvalidDataException($"Archive entry is missing: {file.RelativePath}");
                var destination = ResolveWithin(task.TaskDirectory, file.RelativePath);
                if (File.Exists(destination) && await HashFileAsync(destination, cancellationToken) == file.Sha256)
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".restore-tmp";
                await using (var input = entry.Open())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, cancellationToken);
                var restoredHash = await HashFileAsync(temporary, cancellationToken);
                if (!restoredHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temporary);
                    throw new InvalidDataException($"Restored file hash mismatch: {file.RelativePath}");
                }
                File.Move(temporary, destination, overwrite: true);
                restoredFiles++;
                restoredBytes += file.Size;
            }
            var restoredAt = TimeProvider.GetUtcNow();
            await WriteJsonAtomicAsync(manifestPath, manifest with { RestoredAt = restoredAt }, cancellationToken);
        }
        var at = TimeProvider.GetUtcNow();
        await WriteJsonAtomicAsync(pointerPath, pointer with { RestoredAt = at }, cancellationToken);
        return new RestoreResult(task.TaskKey, restoredFiles, restoredBytes, at);
    }

    public string GetManifestPath(RetentionTaskInventory task, DateTimeOffset archivedAt) =>
        Path.Combine(
            ArchivePath,
            SafeSegment(task.Project),
            SafeSegment(task.TaskKey),
            archivedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'"),
            "manifest.json");

    private async Task<RetentionTaskInventory> ReadTaskAsync(
        string project,
        string taskDirectory,
        string taskJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(taskJson, cancellationToken));
        var root = document.RootElement;
        var key = String(root, "key") ?? String(root, "taskKey") ?? String(root, "id") ?? Path.GetFileName(taskDirectory);
        var id = String(root, "id") ?? key;
        var lane = String(root, "state") ?? String(root, "lane") ?? Path.GetFileName(Path.GetDirectoryName(taskDirectory)!);
        DateTimeOffset? terminalAt = null;
        if (lane is "6-completed" or "7-archive")
            terminalAt = Date(root, "terminalAt") ?? Date(root, "completedAt") ?? Date(root, "enteredLaneAt")
                         ?? Date(root, "laneEnteredAt") ?? Date(root, "updatedAt") ?? Date(root, "lastActivity");
        var files = Directory.EnumerateFiles(taskDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                           && !path.EndsWith(".restore-tmp", StringComparison.OrdinalIgnoreCase))
            .Select(path => InventoryFile(taskDirectory, path))
            .ToList();
        var pointerPath = Path.Combine(taskDirectory, "archive-manifest.json");
        var pointer = await ReadJsonAsync<ArchivePointer>(pointerPath, cancellationToken);
        return new RetentionTaskInventory(
            key, id, project, lane, terminalAt, taskDirectory, files,
            File.Exists(pointerPath), pointer?.RestoredAt);
    }

    private List<RetentionFileInventory> EnumerateWorkspaceRuntimeFiles()
    {
        var paths = new List<string>();
        var metadata = Path.Combine(WorkspacePath, ".metadata");
        if (Directory.Exists(metadata))
            paths.AddRange(Directory.EnumerateFiles(metadata, "attempt-authority*", SearchOption.TopDirectoryOnly));
        var bus = Path.Combine(WorkspacePath, "logs", "bus");
        if (Directory.Exists(bus)) paths.AddRange(Directory.EnumerateFiles(bus, "*", SearchOption.AllDirectories));
        return paths.Select(path => InventoryFile(WorkspacePath, path)).ToList();
    }

    private static RetentionFileInventory InventoryFile(string root, string path)
    {
        var file = new FileInfo(path);
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return new RetentionFileInventory(relative, file.Length, file.LastWriteTimeUtc, ArtifactClassifier.Classify(relative));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static string? String(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement root, string property) =>
        String(root, property) is { } text && DateTimeOffset.TryParse(text, out var value) ? value : null;

    private static string ResolveWithin(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes retention root: {relativePath}");
        return full;
    }

    private static bool IsBelow(string candidate, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindGitRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static string SafeSegment(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Replace('/', '_').Replace('\\', '_');
    }

    private static void RemoveEmptyDirectories(string taskDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(taskDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static void DeleteHotFiles(RetentionTaskInventory task, IEnumerable<ArchiveManifestFile> files)
    {
        foreach (var file in files)
        {
            var source = ResolveWithin(task.TaskDirectory, file.RelativePath);
            if (File.Exists(source)) File.Delete(source);
        }
        RemoveEmptyDirectories(task.TaskDirectory);
    }
}
