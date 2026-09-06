using System.Diagnostics;
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
    int ColdPayloadCount,
    IReadOnlyList<FullBackupFile> Files,
    string SetSha256);
public sealed record FullBackupComplete(DateTimeOffset CompletedAt, string InventorySha256, string SetSha256);
public sealed record FullBackupResult(string BackupDirectory, FullBackupInventory Inventory);

public sealed class FullBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileTreeRetentionStore _store;
    private readonly TimeProvider _timeProvider;

    public FullBackupService(FileTreeRetentionStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FullBackupResult> CreateAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        var createdAt = _timeProvider.GetUtcNow();
        var directory = Path.GetFullPath(outputDirectory);
        var workspacePrefix = _store.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The full backup path must be outside the workspace repository.", nameof(outputDirectory));
        if (Directory.Exists(directory)) throw new IOException($"Backup set already exists: {directory}");
        Directory.CreateDirectory(directory);

        var bundlePath = Path.Combine(directory, "repository.bundle");
        RunGit(_store.WorkspacePath, ["bundle", "create", bundlePath, "--all"]);
        await CopyUntrackedAsync(directory, cancellationToken);
        var coldCount = await CopyReferencedColdAsync(directory, cancellationToken);
        var tasks = await _store.EnumerateTasksAndFilesAsync(cancellationToken: cancellationToken);
        await WriteAnalysisExportAsync(directory, tasks, cancellationToken);

        var files = await InventoryFilesAsync(directory, cancellationToken);
        var setHash = HashSet(files);
        var inventory = new FullBackupInventory(
            1, createdAt, Path.GetFileName(_store.WorkspacePath),
            tasks.Count(task => task.TaskKey != "__workspace-runtime__"), coldCount, files, setHash);
        var inventoryPath = Path.Combine(directory, "inventory.json");
        await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(inventory, JsonOptions) + Environment.NewLine, cancellationToken);
        var inventoryHash = await HashFileAsync(inventoryPath, cancellationToken);
        var complete = new FullBackupComplete(_timeProvider.GetUtcNow(), inventoryHash, setHash);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "complete.json"), JsonSerializer.Serialize(complete, JsonOptions) + Environment.NewLine, cancellationToken);
        return new FullBackupResult(directory, inventory);
    }

    public static async Task<FullBackupInventory> VerifyAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(backupDirectory);
        var completePath = Path.Combine(root, "complete.json");
        var inventoryPath = Path.Combine(root, "inventory.json");
        if (!File.Exists(completePath)) throw new InvalidDataException("Backup set is incomplete: complete.json is missing.");
        var complete = JsonSerializer.Deserialize<FullBackupComplete>(await File.ReadAllTextAsync(completePath, cancellationToken), JsonOptions)
                       ?? throw new InvalidDataException("complete.json is invalid.");
        var inventory = JsonSerializer.Deserialize<FullBackupInventory>(await File.ReadAllTextAsync(inventoryPath, cancellationToken), JsonOptions)
                        ?? throw new InvalidDataException("inventory.json is invalid.");
        if (!complete.InventorySha256.Equals(await HashFileAsync(inventoryPath, cancellationToken), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Inventory hash mismatch.");
        foreach (var file in inventory.Files)
        {
            var path = ResolveWithin(root, file.RelativePath);
            if (!File.Exists(path) || !file.Sha256.Equals(await HashFileAsync(path, cancellationToken), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup file hash mismatch: {file.RelativePath}");
        }
        var actual = await InventoryFilesAsync(root, cancellationToken);
        if (!actual.Select(file => file.RelativePath).SequenceEqual(
                inventory.Files.Select(file => file.RelativePath), StringComparer.Ordinal)
            || !HashSet(actual).Equals(inventory.SetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup set contains missing, changed, or unlisted members.");
        if (!inventory.SetSha256.Equals(HashSet(inventory.Files), StringComparison.OrdinalIgnoreCase)
            || !complete.SetSha256.Equals(inventory.SetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup set hash mismatch.");
        return inventory;
    }

    public static async Task RestoreAsync(
        string backupDirectory,
        string emptyDestination,
        CancellationToken cancellationToken = default)
    {
        await VerifyAsync(backupDirectory, cancellationToken);
        var destination = Path.GetFullPath(emptyDestination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Full restore destination must be empty.");
        Directory.CreateDirectory(destination);
        var workspace = Path.Combine(destination, "workspace");
        RunGit(destination, ["clone", Path.Combine(Path.GetFullPath(backupDirectory), "repository.bundle"), workspace]);
        await CopyTreeAsync(Path.Combine(backupDirectory, "untracked"), workspace, cancellationToken);
        var archive = Path.Combine(destination, "archive");
        await CopyTreeAsync(Path.Combine(backupDirectory, "cold"), archive, cancellationToken);
        await RewriteArchivePointersAsync(workspace, archive, cancellationToken);
    }

    private async Task CopyUntrackedAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var tracked = RunGit(_store.WorkspacePath, ["ls-files", "-z"])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_store.WorkspacePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_store.WorkspacePath, path);
            if (relative.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || tracked.Contains(relative)) continue;
            await CopyFileAsync(path, Path.Combine(backupDirectory, "untracked", relative), cancellationToken);
        }
    }

    private async Task<int> CopyReferencedColdAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pointerPath in Directory.EnumerateFiles(_store.WorkspacePath, "archive-manifest.json", SearchOption.AllDirectories))
        {
            var pointer = JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(pointerPath, cancellationToken), JsonOptions);
            if (pointer == null) continue;
            foreach (var value in pointer.ManifestPaths)
            {
                var manifest = Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(_store.WorkspacePath, value));
                var payload = Path.Combine(Path.GetDirectoryName(manifest)!, "payload.zip");
                foreach (var source in new[] { manifest, payload })
                {
                    if (!File.Exists(source)) throw new InvalidDataException($"Referenced cold file is missing: {source}");
                    var relative = Path.GetRelativePath(_store.ArchivePath, source);
                    if (relative.StartsWith("..", StringComparison.Ordinal))
                        throw new InvalidDataException($"Referenced cold file is outside archive path: {source}");
                    await CopyFileAsync(source, Path.Combine(backupDirectory, "cold", relative), cancellationToken);
                }
                payloads.Add(payload);
            }
        }
        return payloads.Count;
    }

    private static async Task WriteAnalysisExportAsync(
        string backupDirectory,
        IReadOnlyList<RetentionTaskInventory> tasks,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(backupDirectory, "analysis", "tasks.v1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        foreach (var task in tasks.Where(task => task.TaskKey != "__workspace-runtime__"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                task.TaskKey,
                task.TaskId,
                task.Project,
                task.Lane,
                task.TerminalAt,
                HotBytes = task.Files.Sum(file => file.Size),
            }, JsonOptions));
        }
    }

    private static async Task<List<FullBackupFile>> InventoryFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new List<FullBackupFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative is "inventory.json" or "complete.json") continue;
            files.Add(new FullBackupFile(relative, new FileInfo(path).Length, await HashFileAsync(path, cancellationToken)));
        }
        return files;
    }

    private static string HashSet(IEnumerable<FullBackupFile> files)
    {
        var canonical = string.Join('\n', files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => $"{file.RelativePath}\t{file.Size}\t{file.Sha256}")) + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            await CopyFileAsync(path, Path.Combine(destination, Path.GetRelativePath(source, path)), cancellationToken);
    }

    private static async Task RewriteArchivePointersAsync(string workspace, string archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(archive)) return;
        var manifests = Directory.EnumerateFiles(archive, "manifest.json", SearchOption.AllDirectories)
            .ToDictionary(path => ArchiveSuffix(path), StringComparer.OrdinalIgnoreCase);
        foreach (var pointerPath in Directory.EnumerateFiles(workspace, "archive-manifest.json", SearchOption.AllDirectories))
        {
            var pointer = JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(pointerPath, cancellationToken), JsonOptions);
            if (pointer == null) continue;
            var rewritten = pointer.ManifestPaths.Select(path =>
            {
                var suffix = ArchiveSuffix(path.Replace('\\', '/'));
                return manifests.TryGetValue(suffix, out var restored) ? restored : path;
            }).ToList();
            var current = rewritten.LastOrDefault() ?? pointer.ManifestPath;
            await File.WriteAllTextAsync(pointerPath,
                JsonSerializer.Serialize(pointer with { ManifestPath = current, ManifestPaths = rewritten }, JsonOptions) + Environment.NewLine,
                cancellationToken);
        }
    }

    private static string ArchiveSuffix(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.TakeLast(Math.Min(4, parts.Length)));
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ResolveWithin(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Backup path escapes set: {relative}");
        return path;
    }

    private static string RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} failed: {error.Trim()}");
        return output;
    }
}
