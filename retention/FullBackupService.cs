using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Retention;

public sealed record FullBackupFile(string RelativePath, long Size, string Sha256);

public sealed record FullBackupInventory(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string WorkspaceName,
    int TaskCount,
    int ColdManifestCount,
    IReadOnlyList<FullBackupFile> Files,
    string SetSha256);

public sealed record FullBackupComplete(DateTimeOffset CompletedAt, string SetSha256);

public sealed record FullBackupResult(
    string BackupDirectory,
    int TaskCount,
    int FileCount,
    long TotalBytes,
    string SetSha256);

public sealed class FullBackupService
{
    public async Task<FullBackupResult> CreateAsync(
        FileTreeRetentionStore store,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (IsWithin(output, store.WorkspaceRoot))
            throw new ArgumentException("Full backup output must be outside the workspace.", nameof(outputDirectory));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Full backup output directory must be empty.");
        Directory.CreateDirectory(output);

        var bundle = Path.Combine(output, "workspace.bundle");
        RunGit(store.WorkspaceRoot, ["bundle", "create", bundle, "--all"]);
        await CreateUntrackedArchiveAsync(store.WorkspaceRoot,
            Path.Combine(output, "untracked-evidence.zip"), cancellationToken);
        await CreateWorkingTreeOverlayAsync(store.WorkspaceRoot,
            Path.Combine(output, "working-tree-overlay.zip"), cancellationToken);
        await WriteDeletedPathsAsync(store.WorkspaceRoot,
            Path.Combine(output, "deleted-paths.json"), cancellationToken);

        var tasks = await store.EnumerateTasksAndFilesAsync(cancellationToken: cancellationToken);
        var exportDirectory = Path.Combine(output, "export");
        Directory.CreateDirectory(exportDirectory);
        await File.WriteAllLinesAsync(Path.Combine(exportDirectory, "tasks.jsonl"),
            tasks.Where(task => task.Key != "__workspace__").Select(task => JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                task.Id,
                task.Key,
                task.Project,
                task.Lane,
                task.TerminalAt,
                hotBytes = task.Files.Sum(file => file.Size),
            }, RetentionPolicy.JsonOptions)), cancellationToken);

        var coldManifestCount = await CopyReferencedColdStateAsync(
            store, tasks, Path.Combine(output, "cold"), cancellationToken);
        var files = await InventoryFilesAsync(output, cancellationToken);
        var setHash = ComputeSetHash(files);
        var inventory = new FullBackupInventory(
            1, DateTimeOffset.UtcNow, Path.GetFileName(store.WorkspaceRoot),
            tasks.Count(task => task.Key != "__workspace__"), coldManifestCount, files, setHash);
        await File.WriteAllTextAsync(Path.Combine(output, "inventory.json"),
            JsonSerializer.Serialize(inventory, RetentionPolicy.JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "complete.json"),
            JsonSerializer.Serialize(new FullBackupComplete(DateTimeOffset.UtcNow, setHash), RetentionPolicy.JsonOptions),
            cancellationToken);
        return new FullBackupResult(output, inventory.TaskCount, files.Count, files.Sum(file => file.Size), setHash);
    }

    public async Task<FullBackupInventory> VerifyAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(backupDirectory);
        var inventoryPath = Path.Combine(root, "inventory.json");
        var completePath = Path.Combine(root, "complete.json");
        if (!File.Exists(inventoryPath) || !File.Exists(completePath))
            throw new InvalidDataException("Backup is incomplete: inventory.json or complete.json is missing.");
        var inventory = JsonSerializer.Deserialize<FullBackupInventory>(
            await File.ReadAllTextAsync(inventoryPath, cancellationToken), RetentionPolicy.JsonOptions)
            ?? throw new InvalidDataException("Backup inventory is invalid.");
        var complete = JsonSerializer.Deserialize<FullBackupComplete>(
            await File.ReadAllTextAsync(completePath, cancellationToken), RetentionPolicy.JsonOptions)
            ?? throw new InvalidDataException("Backup completion marker is invalid.");
        var actualFiles = await InventoryFilesAsync(root, cancellationToken);
        if (actualFiles.Count != inventory.Files.Count
            || actualFiles.Zip(inventory.Files).Any(pair => pair.First != pair.Second))
        {
            throw new InvalidDataException("Backup file inventory does not match the files in the set.");
        }
        var setHash = ComputeSetHash(actualFiles);
        if (!setHash.Equals(inventory.SetSha256, StringComparison.OrdinalIgnoreCase)
            || !setHash.Equals(complete.SetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup set SHA-256 does not match its completion marker.");
        return inventory;
    }

    public async Task RestoreAsync(
        string backupDirectory,
        string destination,
        CancellationToken cancellationToken = default)
    {
        await VerifyAsync(backupDirectory, cancellationToken);
        var root = Path.GetFullPath(backupDirectory);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Full restore destination must be empty.");
        if (Directory.Exists(target))
            Directory.Delete(target);
        RunGit(Directory.GetParent(target)?.FullName ?? Path.GetTempPath(),
            ["clone", Path.Combine(root, "workspace.bundle"), target]);
        ExtractOverlay(Path.Combine(root, "working-tree-overlay.zip"), target);
        ExtractOverlay(Path.Combine(root, "untracked-evidence.zip"), target);
        var deletedPathsFile = Path.Combine(root, "deleted-paths.json");
        if (File.Exists(deletedPathsFile))
        {
            var deletedPaths = JsonSerializer.Deserialize<string[]>(
                await File.ReadAllTextAsync(deletedPathsFile, cancellationToken), RetentionPolicy.JsonOptions) ?? [];
            foreach (var relativePath in deletedPaths)
            {
                var path = SafePath(target, relativePath);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        var coldSource = Path.Combine(root, "cold");
        var coldTarget = Path.Combine(Directory.GetParent(target)?.FullName ?? target, "agent-taskboard-archive");
        if (Directory.Exists(coldSource))
            CopyDirectory(coldSource, coldTarget);
        await RewriteColdPointersAsync(target, coldTarget, cancellationToken);
    }

    private static async Task<int> CopyReferencedColdStateAsync(
        FileTreeRetentionStore store,
        IReadOnlyList<RetentionTaskInventory> tasks,
        string coldRoot,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var task in tasks.Where(task => task.Key != "__workspace__"))
        {
            var pointerPath = Path.Combine(task.StorePath, "archive-manifest.json");
            if (!File.Exists(pointerPath))
                continue;
            var pointer = JsonSerializer.Deserialize<ArchiveManifestPointer>(
                await File.ReadAllTextAsync(pointerPath, cancellationToken), RetentionPolicy.JsonOptions);
            if (pointer is null)
                continue;
            var manifestPaths = pointer.ManifestPaths.Count > 0 ? pointer.ManifestPaths : [pointer.ManifestPath];
            foreach (var manifestValue in manifestPaths)
            {
                var manifestPath = Path.GetFullPath(manifestValue);
                if (!IsWithin(manifestPath, store.ArchiveRoot))
                    throw new InvalidDataException($"Archive pointer for {task.Key} leaves the archive root.");
                var manifest = JsonSerializer.Deserialize<RetentionArchiveManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken), RetentionPolicy.JsonOptions)
                    ?? throw new InvalidDataException($"Archive manifest for {task.Key} is invalid.");
                foreach (var source in new[] { manifestPath, manifest.PayloadPath })
                {
                    var fullSource = Path.GetFullPath(source);
                    if (!IsWithin(fullSource, store.ArchiveRoot))
                        throw new InvalidDataException($"Archive payload for {task.Key} leaves the archive root.");
                    var relative = Path.GetRelativePath(store.ArchiveRoot, fullSource);
                    var target = SafePath(coldRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(fullSource, target, overwrite: true);
                }
                count++;
            }
        }
        return count;
    }

    private static async Task CreateUntrackedArchiveAsync(
        string workspaceRoot,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var visible = RunGit(workspaceRoot, ["ls-files", "--others", "--exclude-standard", "-z"], capture: true);
        var ignored = RunGit(workspaceRoot, ["ls-files", "--others", "--ignored", "--exclude-standard", "-z"], capture: true);
        var classifier = new ArtifactClassifier();
        var paths = (visible + ignored).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => classifier.Classify(path).ArtifactClass != ArtifactClass.Runtime);
        await CreateZipAsync(workspaceRoot, archivePath, paths, cancellationToken);
    }

    private static async Task CreateWorkingTreeOverlayAsync(
        string workspaceRoot,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var output = RunGit(workspaceRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=no"], capture: true);
        var paths = new List<string>();
        foreach (var item in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length <= 3)
                continue;
            var path = item[3..];
            if (File.Exists(Path.Combine(workspaceRoot, path)))
                paths.Add(path);
        }
        await CreateZipAsync(workspaceRoot, archivePath, paths, cancellationToken);
    }

    private static async Task WriteDeletedPathsAsync(
        string workspaceRoot,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var output = RunGit(workspaceRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=no"], capture: true);
        var deleted = output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Length > 3 && (item[0] == 'D' || item[1] == 'D'))
            .Select(item => item[3..].Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(deleted, RetentionPolicy.JsonOptions), cancellationToken);
    }

    private static async Task CreateZipAsync(
        string root,
        string archivePath,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(archivePath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var relative in paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = SafePath(root, relative);
            if (!File.Exists(source))
                continue;
            var entry = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.SmallestSize);
            await using var output = entry.Open();
            await using var input = File.OpenRead(source);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<FullBackupFile>> InventoryFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<FullBackupFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetFileName(path).Equals("inventory.json", StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(path).Equals("complete.json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            files.Add(new FullBackupFile(
                Path.GetRelativePath(root, path).Replace('\\', '/'), stream.Length, hash));
        }
        return files;
    }

    private static string ComputeSetHash(IEnumerable<FullBackupFile> files)
    {
        var canonical = string.Join('\n', files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => $"{file.RelativePath}\0{file.Size}\0{file.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task RewriteColdPointersAsync(
        string workspaceRoot,
        string coldRoot,
        CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(workspaceRoot, "archive-manifest.json", SearchOption.AllDirectories))
        {
            var pointer = JsonSerializer.Deserialize<ArchiveManifestPointer>(
                await File.ReadAllTextAsync(path, cancellationToken), RetentionPolicy.JsonOptions);
            if (pointer is null)
                continue;
            var old = pointer.ManifestPath.Replace('\\', '/');
            var segments = old.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var projectIndex = Math.Max(0, segments.Length - 4);
            var relative = Path.Combine(segments[projectIndex..]);
            var manifestPath = Path.Combine(coldRoot, relative);
            if (!File.Exists(manifestPath))
                continue;
            var rewrittenPaths = new List<string>();
            foreach (var oldPath in pointer.ManifestPaths.Count > 0 ? pointer.ManifestPaths : [pointer.ManifestPath])
            {
                var oldSegments = oldPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var oldProjectIndex = Math.Max(0, oldSegments.Length - 4);
                var rewritten = Path.Combine(coldRoot, Path.Combine(oldSegments[oldProjectIndex..]));
                rewrittenPaths.Add(rewritten);
                if (!File.Exists(rewritten))
                    continue;
                var archiveManifest = JsonSerializer.Deserialize<RetentionArchiveManifest>(
                    await File.ReadAllTextAsync(rewritten, cancellationToken), RetentionPolicy.JsonOptions)!;
                var updatedManifest = archiveManifest with
                {
                    PayloadPath = Path.Combine(Path.GetDirectoryName(rewritten)!, "payload.zip"),
                };
                await File.WriteAllTextAsync(rewritten,
                    JsonSerializer.Serialize(updatedManifest, RetentionPolicy.JsonOptions), cancellationToken);
            }
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(pointer with { ManifestPath = manifestPath, ManifestPaths = rewrittenPaths }, RetentionPolicy.JsonOptions), cancellationToken);
        }
    }

    private static string RunGit(string workingDirectory, IReadOnlyList<string> arguments, bool capture = false)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed: {error.Trim()}");
        return capture ? output : string.Empty;
    }

    private static void ExtractOverlay(string archivePath, string destination)
    {
        if (File.Exists(archivePath))
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string SafePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root))
            throw new InvalidDataException($"Backup path escapes its root: {relative}");
        return path;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
