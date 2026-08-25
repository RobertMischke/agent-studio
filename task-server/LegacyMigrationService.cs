using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class LegacyMigrationService(TaskServerStore store)
{
    public async Task<LegacyMigrationInventory> InventoryAsync(LegacyMigrationRequest request, CancellationToken ct)
    {
        var root = ResolveLegacyRoot(request.LegacyRoot);
        var scan = await ScanAsync(root, includeContent: false, ct);
        return new LegacyMigrationInventory(
            scan.MigrationId,
            root,
            scan.Projects.Count,
            scan.Projects.Sum(project => project.Tasks.Count),
            scan.EventCount,
            scan.ArtifactCount,
            scan.EvidenceGitRoots,
            scan.Warnings);
    }

    public async Task<LegacyMigrationResult> ImportAsync(LegacyMigrationRequest request, string actorId, CancellationToken ct)
    {
        if (!request.FreezeConfirmed)
            throw new TaskServerConflictException(
                "legacy-freeze-required",
                "Legacy import requires an explicit single-writer freeze confirmation.");
        if (store.Mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException(
                "maintenance-required",
                "Legacy import requires Task Server maintenance mode.");
        if (string.IsNullOrWhiteSpace(request.ExpectedMigrationId))
            throw new TaskServerConflictException(
                "legacy-inventory-required",
                "Legacy import requires the migration id from a completed inventory.");

        var root = ResolveLegacyRoot(request.LegacyRoot);
        var scan = await ScanAsync(root, includeContent: true, ct);
        if (!string.Equals(request.ExpectedMigrationId, scan.MigrationId, StringComparison.Ordinal))
        {
            throw new TaskServerConflictException(
                "legacy-inventory-changed",
                $"The frozen legacy source no longer matches inventory '{request.ExpectedMigrationId}'. " +
                $"Its current migration id is '{scan.MigrationId}'. Inventory it again before import.");
        }
        var backup = await store.CreateBackupAsync(new BackupRequest("before-legacy-import"), actorId, ct);
        await store.ImportLegacyBatchAsync(request.WorkspaceName, scan.Projects, scan.Authority, scan.MigrationId, actorId, ct);

        if (request.PreserveEvidenceGit)
            await PreserveEvidenceGitAsync(scan.MigrationId, scan.EvidenceGitRoots, ct);

        var digest = await store.ComputeIntegrityDigestAsync(ct);
        return new LegacyMigrationResult(
            scan.MigrationId,
            true,
            scan.Projects.Count,
            scan.Projects.Sum(project => project.Tasks.Count),
            scan.EventCount,
            scan.ArtifactCount,
            digest,
            $"Restore backup '{backup.BackupId}' before enabling the new writer. The frozen legacy root remains untouched.",
            scan.EvidenceGitRoots);
    }

    private static string ResolveLegacyRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Legacy root is required.");
        var root = Path.GetFullPath(value);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Legacy root '{root}' does not exist.");
        return root;
    }

    private static async Task<LegacyScan> ScanAsync(string root, bool includeContent, CancellationToken ct)
    {
        var warnings = new List<string>();
        var taskFiles = Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var byProject = new Dictionary<string, List<LegacyTaskImport>>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new HashSet<string>(taskFiles, StringComparer.Ordinal);
        var authorityPath = Path.Combine(root, ".metadata", "attempt-authority.json");
        if (File.Exists(authorityPath)) sourceFiles.Add(authorityPath);
        var authorityArchives = Directory.Exists(Path.GetDirectoryName(authorityPath))
            ? Directory.EnumerateFiles(
                Path.GetDirectoryName(authorityPath)!,
                "attempt-authority.archive-*.json",
                SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : [];
        foreach (var archive in authorityArchives) sourceFiles.Add(archive);
        var eventCount = 0;
        var artifactCount = 0;

        foreach (var jobFile in taskFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(jobFile);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var rootElement = document.RootElement;
                var taskDirectory = Path.GetDirectoryName(jobFile)!;
                var taskKey = ReadString(rootElement, "id")
                    ?? ReadString(rootElement, "jobId")
                    ?? Path.GetFileName(taskDirectory);
                taskKey = taskKey.Trim().ToUpperInvariant();
                var title = ReadString(rootElement, "title") ?? taskKey;
                var state = ReadString(rootElement, "state") ?? InferState(taskDirectory);
                var body = await ReadBodyAsync(rootElement, taskDirectory, includeContent, ct);
                AddIfPresent(sourceFiles, Path.Combine(taskDirectory, "prompt.md"));
                AddIfPresent(sourceFiles, Path.Combine(taskDirectory, "timeline.jsonl"));
                var resultsDirectory = Path.Combine(taskDirectory, "results");
                if (Directory.Exists(resultsDirectory))
                    foreach (var resultFile in Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories))
                        sourceFiles.Add(resultFile);
                var info = new FileInfo(jobFile);
                var created = ReadDate(rootElement, "createdAt") ?? info.CreationTimeUtc;
                var updated = ReadDate(rootElement, "updatedAt") ?? info.LastWriteTimeUtc;
                var projectName = InferProjectName(root, taskDirectory, rootElement, taskKey);
                var projectId = TaskServerStore.DeterministicId("prj", projectName);
                var taskId = TaskServerStore.DeterministicId("tsk", $"{projectId}:{taskKey}");
                var events = await ReadEventsAsync(taskDirectory, taskId, includeContent, ct);
                var artifacts = await ReadArtifactsAsync(taskDirectory, taskId, includeContent, ct);
                eventCount += events.Count;
                artifactCount += artifacts.Count;
                if (!byProject.TryGetValue(projectName, out var tasks))
                    byProject[projectName] = tasks = [];
                tasks.Add(new LegacyTaskImport(taskId, taskKey, title, body, state, created, updated, events, artifacts));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Skipped unreadable task metadata '{Path.GetRelativePath(root, jobFile)}': {exception.Message}");
            }
        }

        var projects = byProject.Select(pair =>
        {
            var prefix = pair.Value.Select(task => task.TaskKey.Split('-', 2)[0]).FirstOrDefault() ?? "LEG";
            var next = pair.Value.Select(task => ParseTaskNumber(task.TaskKey)).DefaultIfEmpty(0).Max() + 1L;
            return new LegacyProjectImport(TaskServerStore.DeterministicId("prj", pair.Key), pair.Key, prefix, next, pair.Value);
        }).OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
        var evidenceGitRoots = FindEvidenceGitRoots(root);
        var authority = includeContent && File.Exists(authorityPath)
            ? await ReadAuthorityAsync(authorityPath, authorityArchives, ct)
            : null;
        var identity = new StringBuilder(root);
        foreach (var file in sourceFiles.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            await using var stream = File.OpenRead(file);
            var digest = await SHA256.HashDataAsync(stream, ct);
            identity.Append('|')
                .Append(Path.GetRelativePath(root, file))
                .Append(':')
                .Append(info.Length)
                .Append(':')
                .Append(Convert.ToHexString(digest));
        }
        var migrationId = TaskServerStore.DeterministicId("mig", identity.ToString());
        return new LegacyScan(migrationId, projects, eventCount, artifactCount, evidenceGitRoots, warnings, authority);
    }

    private static async Task<LegacyAuthorityImport> ReadAuthorityAsync(
        string path,
        IReadOnlyList<string> archivePaths,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        var fences = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("lastFenceByTask", out var fenceObject))
            foreach (var property in fenceObject.EnumerateObject())
                fences[property.Name.ToUpperInvariant()] = property.Value.GetInt64();

        var runs = root.TryGetProperty("runAttempts", out var runArray)
            ? runArray.EnumerateArray().Select(ReadRunAuthority).ToArray()
            : [];
        var reviews = root.TryGetProperty("reviewAttempts", out var reviewArray)
            ? reviewArray.EnumerateArray().Select(ReadReviewAuthority).ToArray()
            : [];
        var allRuns = runs.ToDictionary(item => item.AttemptId, StringComparer.OrdinalIgnoreCase);
        var allReviews = reviews.ToDictionary(item => item.AttemptId, StringComparer.OrdinalIgnoreCase);
        foreach (var archivePath in archivePaths)
        {
            await using var archiveStream = File.OpenRead(archivePath);
            using var archive = await JsonDocument.ParseAsync(archiveStream, cancellationToken: ct);
            if (archive.RootElement.TryGetProperty("runAttempts", out var archivedRuns))
                foreach (var item in archivedRuns.EnumerateArray().Select(ReadRunAuthority))
                    allRuns.TryAdd(item.AttemptId, item);
            if (archive.RootElement.TryGetProperty("reviewAttempts", out var archivedReviews))
                foreach (var item in archivedReviews.EnumerateArray().Select(ReadReviewAuthority))
                    allReviews.TryAdd(item.AttemptId, item);
        }
        return new LegacyAuthorityImport(
            ReadLong(root, "authorityEpoch", 1),
            fences,
            allRuns.Values.OrderBy(item => item.CreatedAt).ToArray(),
            allReviews.Values.OrderBy(item => item.CreatedAt).ToArray());
    }

    private static LegacyRunAuthority ReadRunAuthority(JsonElement item) => new(
        Required(item, "attemptId"),
        Required(item, "taskKey").ToUpperInvariant(),
        ReadString(item, "repositoryId") ?? "legacy",
        ReadState(item),
        ReadLease(item),
        ReadLong(item, "lastFence", 0),
        ReadLong(item, "authorityEpoch", 1),
        ReadDate(item, "createdAt") ?? DateTime.UnixEpoch,
        ReadDate(item, "terminalAt"),
        ReadString(item, "resultSha"),
        ReadString(item, "terminalOutcome"));

    private static LegacyReviewAuthority ReadReviewAuthority(JsonElement item)
    {
        var subject = item.TryGetProperty("subject", out var value) ? value : default;
        JsonElement? plan = subject.ValueKind == JsonValueKind.Object
                            && subject.TryGetProperty("plan", out var planValue)
            ? planValue.Clone()
            : null;
        var reports = item.TryGetProperty("reports", out var reportArray)
            ? reportArray.EnumerateArray().Select(report => new LegacyReviewReportAuthority(
                ReadString(report, "idempotencyKey")
                    ?? $"legacy-report-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(report.GetRawText()))).ToLowerInvariant()}",
                report.GetRawText(),
                ReadDate(report, "receivedAt") ?? DateTime.UnixEpoch)).ToArray()
            : [];
        return new LegacyReviewAuthority(
            Required(item, "attemptId"),
            Required(item, "taskKey").ToUpperInvariant(),
            ReadString(item, "repositoryId") ?? ReadString(subject, "repositoryId") ?? "legacy",
            Required(item, "sourceRunAttemptId"),
            ReadString(subject, "subjectId") ?? $"legacy-subject-{Required(item, "attemptId")}",
            ReadString(subject, "expectedResultSha") ?? "unknown",
            ReadString(subject, "reviewPolicyHash") ?? "legacy",
            ReadString(subject, "repositoryUrl"),
            ReadString(subject, "resultRef"),
            plan,
            ReadState(item),
            ReadLease(item),
            ReadLong(item, "lastFence", 0),
            ReadLong(item, "authorityEpoch", 1),
            ReadDate(item, "createdAt") ?? DateTime.UnixEpoch,
            ReadDate(item, "terminalAt"),
            ReadEnumText(item, "outcome"),
            ReadString(item, "failureClassification"),
            reports);
    }

    private static LegacyLeaseAuthority? ReadLease(JsonElement item)
    {
        if (!item.TryGetProperty("lease", out var lease) || lease.ValueKind != JsonValueKind.Object) return null;
        return new LegacyLeaseAuthority(
            Required(lease, "leaseId"),
            ReadLong(lease, "fence", 0),
            ReadLong(lease, "authorityEpoch", 1),
            Required(lease, "executorId"),
            ReadString(lease, "hostId") ?? "legacy-host",
            ReadDate(lease, "acquiredAt") ?? DateTime.UnixEpoch,
            ReadDate(lease, "expiresAt") ?? DateTime.UnixEpoch,
            ReadString(lease, "leaseInstanceId") ?? ReadString(lease, "clientId") ?? "legacy-instance");
    }

    private static int ReadState(JsonElement item)
    {
        if (!item.TryGetProperty("state", out var state)) return 0;
        if (state.ValueKind == JsonValueKind.Number) return state.GetInt32();
        return (state.GetString() ?? string.Empty).ToLowerInvariant() switch
        {
            "leased" => 1,
            "completed" => 2,
            "failed" => 3,
            "cancelled" => 4,
            "superseded" => 5,
            _ => 0,
        };
    }

    private static long ReadLong(JsonElement item, string property, long fallback)
        => item.ValueKind == JsonValueKind.Object
           && item.TryGetProperty(property, out var value)
           && value.TryGetInt64(out var result)
            ? result
            : fallback;

    private static string Required(JsonElement item, string property)
        => ReadString(item, property)
           ?? throw new InvalidDataException($"Legacy authority field '{property}' is required.");

    private static string? ReadEnumText(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static async Task<string?> ReadBodyAsync(JsonElement json, string taskDirectory, bool includeContent, CancellationToken ct)
    {
        var body = ReadString(json, "promptMarkdown") ?? ReadString(json, "description");
        if (body is not null || !includeContent) return body;
        var prompt = Path.Combine(taskDirectory, "prompt.md");
        return File.Exists(prompt) ? await File.ReadAllTextAsync(prompt, ct) : null;
    }

    private static void AddIfPresent(ISet<string> files, string path)
    {
        if (File.Exists(path)) files.Add(path);
    }

    private static async Task<IReadOnlyList<LegacyEventImport>> ReadEventsAsync(
        string taskDirectory, string taskId, bool includeContent, CancellationToken ct)
    {
        var timeline = Path.Combine(taskDirectory, "timeline.jsonl");
        if (!File.Exists(timeline)) return [];
        var result = new List<LegacyEventImport>();
        var lines = await File.ReadAllLinesAsync(timeline, ct);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            var key = $"legacy:{taskId}:event:{index}";
            var payload = includeContent ? lines[index] : "{}";
            var kind = "legacy.timeline";
            var occurred = File.GetLastWriteTimeUtc(timeline);
            try
            {
                using var json = JsonDocument.Parse(lines[index]);
                kind = ReadString(json.RootElement, "type") ?? ReadString(json.RootElement, "kind") ?? kind;
                occurred = ReadDate(json.RootElement, "timestamp") ?? ReadDate(json.RootElement, "occurredAt") ?? occurred;
            }
            catch (JsonException)
            {
                kind = "legacy.timeline.unparsed";
            }
            result.Add(new LegacyEventImport(TaskServerStore.DeterministicId("evt", key), kind, payload, key, occurred));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LegacyArtifactImport>> ReadArtifactsAsync(
        string taskDirectory, string taskId, bool includeContent, CancellationToken ct)
    {
        var resultsDirectory = Path.Combine(taskDirectory, "results");
        if (!Directory.Exists(resultsDirectory)) return [];
        var result = new List<LegacyArtifactImport>();
        foreach (var path in Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(resultsDirectory, path).Replace('\\', '/');
            var content = includeContent ? await File.ReadAllBytesAsync(path, ct) : [];
            var sha = includeContent
                ? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
                : string.Empty;
            var key = $"legacy:{taskId}:artifact:{relative}";
            result.Add(new LegacyArtifactImport(
                TaskServerStore.DeterministicId("art", key),
                relative,
                ContentType(relative),
                content,
                sha,
                key,
                File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }

    private async Task PreserveEvidenceGitAsync(string migrationId, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var destinationRoot = Path.Combine(store.DataDirectory, "migration-evidence", migrationId);
        Directory.CreateDirectory(destinationRoot);
        var manifest = new List<object>();
        for (var index = 0; index < roots.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var gitEntry = Path.Combine(roots[index], ".git");
            var destination = Path.Combine(destinationRoot, index.ToString("D3"), ".git");
            if (Directory.Exists(gitEntry))
                await CopyDirectoryAsync(gitEntry, destination, ct);
            else if (File.Exists(gitEntry))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(gitEntry, destination, overwrite: true);
            }
            manifest.Add(new { source = roots[index], preservedAt = Path.GetRelativePath(store.DataDirectory, destination) });
        }
        await File.WriteAllTextAsync(
            Path.Combine(destinationRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            await using var input = File.OpenRead(file);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, ct);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            await CopyDirectoryAsync(directory, Path.Combine(destination, info.Name), ct);
        }
    }

    private static IReadOnlyList<string> FindEvidenceGitRoots(string root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, ".git", SearchOption.AllDirectories))
        {
            var parent = Path.GetDirectoryName(entry);
            if (parent is not null) result.Add(parent);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string InferProjectName(string root, string taskDirectory, JsonElement json, string taskKey)
    {
        var configured = ReadString(json, "projectName") ?? ReadString(json, "project");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var relative = Path.GetRelativePath(root, taskDirectory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectsIndex = Array.FindIndex(relative, part => string.Equals(part, "projects", StringComparison.OrdinalIgnoreCase));
        if (projectsIndex >= 0 && projectsIndex + 1 < relative.Length) return relative[projectsIndex + 1];
        return taskKey.Split('-', 2)[0];
    }

    private static string InferState(string taskDirectory)
    {
        var parent = Directory.GetParent(taskDirectory)?.Name;
        return parent is not null && parent.Length > 2 && char.IsDigit(parent[0]) && parent[1] == '-'
            ? parent
            : "0-backlog";
    }

    private static long ParseTaskNumber(string taskKey)
        => long.TryParse(taskKey.Split('-', 2).ElementAtOrDefault(1), out var value) ? value : 0;

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ReadDate(JsonElement element, string property)
        => DateTime.TryParse(ReadString(element, property), out var value) ? value.ToUniversalTime() : null;

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".md" or ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream",
    };

    private sealed record LegacyScan(
        string MigrationId,
        IReadOnlyList<LegacyProjectImport> Projects,
        int EventCount,
        int ArtifactCount,
        IReadOnlyList<string> EvidenceGitRoots,
        IReadOnlyList<string> Warnings,
        LegacyAuthorityImport? Authority);
}
