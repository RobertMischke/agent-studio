using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed class FileTreeRetentionStore : IRetentionStore
{
    private readonly string _workspaceRoot;
    private readonly string _archiveRoot;
    private readonly ArtifactClassifier _classifier;

    public FileTreeRetentionStore(
        string workspaceRoot,
        string? archiveRoot = null,
        ArtifactClassifier? classifier = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace does not exist: {_workspaceRoot}");
        _archiveRoot = Path.GetFullPath(archiveRoot
            ?? Environment.GetEnvironmentVariable("ARCHIVE_PATH")
            ?? DefaultArchiveRoot(_workspaceRoot));
        if (IsWithin(_archiveRoot, _workspaceRoot))
            throw new ArgumentException("Archive path must be outside the workspace repository.", nameof(archiveRoot));
        _classifier = classifier ?? new ArtifactClassifier();
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string ArchiveRoot => _archiveRoot;

    public Task<IReadOnlyList<RetentionTaskInventory>> EnumerateTasksAndFilesAsync(
        string? project = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<RetentionTaskInventory>();
        var projectsRoot = Path.Combine(_workspaceRoot, "projects");
        if (Directory.Exists(projectsRoot))
        {
            foreach (var projectDirectory in Directory.EnumerateDirectories(projectsRoot)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectName = Path.GetFileName(projectDirectory);
                if (!string.IsNullOrWhiteSpace(project)
                    && !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
                    continue;
                var tasksRoot = Path.Combine(projectDirectory, "tasks");
                if (!Directory.Exists(tasksRoot))
                    continue;
                var metadataPaths = Directory.EnumerateDirectories(tasksRoot)
                    .SelectMany(bucket => Directory.EnumerateDirectories(bucket))
                    .Select(taskDirectory =>
                    {
                        var taskJson = Path.Combine(taskDirectory, "task.json");
                        var jobJson = Path.Combine(taskDirectory, "job.json");
                        return File.Exists(taskJson) ? taskJson : File.Exists(jobJson) ? jobJson : null;
                    })
                    .Where(path => path is not null)
                    .Select(path => path!);
                foreach (var metadataPath in metadataPaths.Order(StringComparer.OrdinalIgnoreCase))
                {
                    var taskRoot = Path.GetDirectoryName(metadataPath)!;
                    var inventory = ReadTask(projectName, taskRoot, metadataPath);
                    if (!string.IsNullOrWhiteSpace(taskKey)
                        && !inventory.Key.Equals(taskKey, StringComparison.OrdinalIgnoreCase)
                        && !inventory.Id.Equals(taskKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    tasks.Add(inventory);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(taskKey))
        {
            var runtime = EnumerateWorkspaceRuntime().ToArray();
            if (runtime.Length > 0)
            {
                tasks.Add(new RetentionTaskInventory(
                    "__workspace__", "__workspace__", "_workspace", "runtime", null,
                    runtime, _workspaceRoot));
            }
        }
        return Task.FromResult<IReadOnlyList<RetentionTaskInventory>>(tasks);
    }

    public Task<Stream> ReadFileAsync(
        RetentionTaskInventory task,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = File.OpenRead(SafePath(task.StorePath, relativePath));
        return Task.FromResult(stream);
    }

    public async Task<ColdArchivePreparation> MoveToColdAsync(
        RetentionTaskInventory task,
        IReadOnlyList<string> relativePaths,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default)
    {
        var stamp = archivedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'");
        var archiveDirectory = SafeArchivePath(task.Project, task.Key, stamp);
        Directory.CreateDirectory(archiveDirectory);
        var payloadPath = Path.Combine(archiveDirectory, "payload.zip");
        if (File.Exists(payloadPath))
            throw new IOException($"Archive payload already exists: {payloadPath}");
        var temporaryPayload = payloadPath + ".tmp";
        var files = new List<ArchiveManifestFile>();
        try
        {
            await using (var destination = new FileStream(
                             temporaryPayload, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var relativePath in relativePaths
                             .Select(Normalize)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = SafePath(task.StorePath, relativePath);
                    if (!File.Exists(sourcePath))
                        continue;
                    await using var source = File.OpenRead(sourcePath);
                    var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken))
                        .ToLowerInvariant();
                    source.Position = 0;
                    var entry = zip.CreateEntry(relativePath, CompressionLevel.SmallestSize);
                    await using var output = entry.Open();
                    await source.CopyToAsync(output, cancellationToken);
                    files.Add(new ArchiveManifestFile(relativePath, source.Length, sha256));
                }
            }
            File.Move(temporaryPayload, payloadPath);
        }
        catch
        {
            if (File.Exists(temporaryPayload))
                File.Delete(temporaryPayload);
            throw;
        }

        await using var payload = File.OpenRead(payloadPath);
        var payloadHash = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken)).ToLowerInvariant();
        return new ColdArchivePreparation(archiveDirectory, payloadPath, payloadHash, files);
    }

    public async Task<string> WriteManifestAsync(
        ColdArchivePreparation preparation,
        RetentionArchiveManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(preparation.ArchiveDirectory, "manifest.json");
        await WriteJsonAtomicAsync(path, manifest, cancellationToken);
        return path;
    }

    public async Task WriteStubAsync(
        RetentionTaskInventory task,
        IReadOnlyList<string> archivedRelativePaths,
        IReadOnlyList<RetentionExcerpt> excerpts,
        ArchiveManifestPointer pointer,
        CancellationToken cancellationToken = default)
    {
        foreach (var excerpt in excerpts)
        {
            var path = SafePath(task.StorePath, excerpt.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, excerpt.Markdown, cancellationToken);
        }
        var pointerPath = Path.Combine(task.StorePath, "archive-manifest.json");
        var previousPaths = Array.Empty<string>();
        if (File.Exists(pointerPath))
        {
            var previous = JsonSerializer.Deserialize<ArchiveManifestPointer>(
                await File.ReadAllTextAsync(pointerPath, cancellationToken), RetentionPolicy.JsonOptions);
            if (previous is not null)
                previousPaths = PointerManifestPaths(previous).ToArray();
        }
        var chainedPointer = pointer with
        {
            ManifestPaths = previousPaths.Append(pointer.ManifestPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        await WriteJsonAtomicAsync(pointerPath, chainedPointer, cancellationToken);
        foreach (var relativePath in archivedRelativePaths)
        {
            var path = SafePath(task.StorePath, relativePath);
            if (File.Exists(path))
                File.Delete(path);
        }
        RemoveEmptyDirectories(task.StorePath);
    }

    public async Task<RetentionArchiveManifest> RestoreAsync(
        string taskKey,
        string? project = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = await EnumerateTasksAndFilesAsync(project, taskKey, cancellationToken);
        var task = tasks.SingleOrDefault(item =>
            item.Key.Equals(taskKey, StringComparison.OrdinalIgnoreCase)
            || item.Id.Equals(taskKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Task was not found: {taskKey}");
        var pointerPath = Path.Combine(task.StorePath, "archive-manifest.json");
        if (!File.Exists(pointerPath))
            throw new InvalidOperationException($"Task is not archived: {taskKey}");
        var pointer = JsonSerializer.Deserialize<ArchiveManifestPointer>(
            await File.ReadAllTextAsync(pointerPath, cancellationToken), RetentionPolicy.JsonOptions)
            ?? throw new InvalidDataException("Archive pointer is invalid.");
        var manifestPaths = PointerManifestPaths(pointer).Select(Path.GetFullPath).ToArray();
        if (manifestPaths.Any(path => !IsWithin(path, _archiveRoot) || !File.Exists(path)))
            throw new InvalidDataException("Archive pointer does not resolve inside the configured archive root.");
        var manifests = new List<(string Path, RetentionArchiveManifest Manifest)>();
        foreach (var manifestPath in manifestPaths)
        {
            var value = JsonSerializer.Deserialize<RetentionArchiveManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken), RetentionPolicy.JsonOptions)
                ?? throw new InvalidDataException("Archive manifest is invalid.");
            manifests.Add((manifestPath, value));
        }
        var manifest = manifests[^1].Manifest;
        if (pointer.RestoredAt is not null)
            return manifest;

        var restoreRoot = Path.Combine(Path.GetTempPath(), "agent-retention-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreRoot);
        try
        {
            foreach (var (_, archiveManifest) in manifests)
            {
                var payloadPath = Path.GetFullPath(archiveManifest.PayloadPath);
                if (!IsWithin(payloadPath, _archiveRoot) || !File.Exists(payloadPath))
                    throw new FileNotFoundException("Archive payload is missing.", payloadPath);
                await VerifyHashAsync(payloadPath, archiveManifest.PayloadSha256, cancellationToken);
                var stageRoot = Path.Combine(restoreRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageRoot);
                ZipFile.ExtractToDirectory(payloadPath, stageRoot);
                foreach (var file in archiveManifest.Files)
                {
                    var restored = SafePath(stageRoot, file.RelativePath);
                    await VerifyHashAsync(restored, file.Sha256, cancellationToken);
                }
                foreach (var file in archiveManifest.Files)
                {
                    var source = SafePath(stageRoot, file.RelativePath);
                    var destination = SafePath(task.StorePath, file.RelativePath);
                    if (File.Exists(destination))
                    {
                        await VerifyHashAsync(destination, file.Sha256, cancellationToken);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                }
            }
        }
        finally
        {
            Directory.Delete(restoreRoot, recursive: true);
        }

        var restoredAt = DateTimeOffset.UtcNow;
        RetentionArchiveManifest? updatedManifest = null;
        foreach (var (manifestPath, archiveManifest) in manifests)
        {
            updatedManifest = archiveManifest with { RestoredAt = restoredAt };
            await WriteJsonAtomicAsync(manifestPath, updatedManifest, cancellationToken);
        }
        await WriteJsonAtomicAsync(pointerPath, pointer with { RestoredAt = restoredAt }, cancellationToken);
        return updatedManifest!;
    }

    private RetentionTaskInventory ReadTask(string project, string taskRoot, string metadataPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = document.RootElement;
        var id = ReadString(root, "id") ?? Path.GetFileName(taskRoot);
        var key = ReadString(root, "key") ?? ReadString(root, "taskKey") ?? id;
        var lane = ReadString(root, "state") ?? ReadString(root, "lane")
            ?? Path.GetFileName(Path.GetDirectoryName(taskRoot)!) ?? "unknown";
        DateTimeOffset? terminalAt = IsTerminal(lane)
            ? ReadDate(root, "terminalAt")
              ?? ReadDate(root, "completedAt")
              ?? ReadDate(root, "enteredLaneAt")
              ?? ReadDate(root, "updatedAt")
              ?? File.GetLastWriteTimeUtc(metadataPath)
            : null;
        var files = Directory.EnumerateFiles(taskRoot, "*", SearchOption.AllDirectories)
            .Select(path => ToInventory(taskRoot, path))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RetentionTaskInventory(id, key, project, lane, terminalAt, files, taskRoot);
    }

    private IEnumerable<RetentionFileInventory> EnumerateWorkspaceRuntime()
    {
        var metadata = Path.Combine(_workspaceRoot, ".metadata");
        if (Directory.Exists(metadata))
        {
            foreach (var path in Directory.EnumerateFiles(metadata, "attempt-authority*", SearchOption.TopDirectoryOnly))
                yield return ToInventory(_workspaceRoot, path);
        }
        var bus = Path.Combine(_workspaceRoot, "logs", "bus");
        if (Directory.Exists(bus))
        {
            foreach (var path in Directory.EnumerateFiles(bus, "*", SearchOption.AllDirectories))
                yield return ToInventory(_workspaceRoot, path);
        }
    }

    private RetentionFileInventory ToInventory(string root, string path)
    {
        var relative = Normalize(Path.GetRelativePath(root, path));
        var classification = _classifier.Classify(relative);
        var info = new FileInfo(path);
        return new RetentionFileInventory(
            relative, info.Length, info.LastWriteTimeUtc,
            classification.ArtifactClass, classification.RuleFamily);
    }

    private string SafeArchivePath(params string[] segments)
    {
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                    || segment is "." or ".."
                                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Archive path contains an invalid segment.");
        var path = Path.GetFullPath(Path.Combine([_archiveRoot, .. segments]));
        if (!IsWithin(path, _archiveRoot))
            throw new InvalidDataException("Archive path escapes the configured archive root.");
        return path;
    }

    private static string SafePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, Normalize(relativePath)));
        if (!IsWithin(path, fullRoot))
            throw new InvalidDataException($"Artifact path escapes its store root: {relativePath}");
        return path;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultArchiveRoot(string workspaceRoot)
    {
        var parent = Directory.GetParent(workspaceRoot)?.FullName
            ?? throw new InvalidOperationException("Workspace root has no parent directory.");
        return Path.Combine(parent, "agent-taskboard-archive");
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var date)
            ? date
            : null;

    private static bool IsTerminal(string lane) =>
        lane.Equals("6-completed", StringComparison.OrdinalIgnoreCase)
        || lane.Equals("7-archive", StringComparison.OrdinalIgnoreCase)
        || lane.Equals("completed", StringComparison.OrdinalIgnoreCase)
        || lane.Equals("archive", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> PointerManifestPaths(ArchiveManifestPointer pointer) =>
        pointer.ManifestPaths.Count > 0 ? pointer.ManifestPaths : [pointer.ManifestPath];

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(value, RetentionPolicy.JsonOptions), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for {path}.");
    }

    private static void RemoveEmptyDirectories(string taskRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(taskRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }
}
