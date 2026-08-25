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
            scan.Warnings,
            scan.Authority.RunAttempts.Count,
            scan.Authority.ReviewAttempts.Count,
            scan.Authority.ActiveAuthorityCount,
            scan.Authority.AuthorityEpoch);
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
        await store.ImportLegacyBatchAsync(request.WorkspaceName, scan.Projects, scan.Authority, actorId, ct);

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
            scan.EvidenceGitRoots,
            scan.Authority.RunAttempts.Count,
            scan.Authority.ReviewAttempts.Count,
            scan.Authority.ActiveAuthorityCount,
            scan.Authority.AuthorityEpoch);
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
        var taskFiles = Directory.EnumerateFiles(root, "task.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
            .GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(path => Path.GetFileName(path) == "task.json" ? 0 : 1).First())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var byProject = new Dictionary<string, List<LegacyTaskImport>>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new HashSet<string>(taskFiles, StringComparer.Ordinal);
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
        var authorityPath = Path.Combine(root, ".metadata", "attempt-authority.json");
        var authority = await ReadAuthorityAsync(authorityPath, ct);
        AddIfPresent(sourceFiles, authorityPath);
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

    private static async Task<LegacyAuthorityImport> ReadAuthorityAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return LegacyAuthorityImport.Empty;

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        var epoch = ReadInt64(root, "authorityEpoch", 1);
        var fences = ReadLongDictionary(root, "lastFenceByTask");
        var runs = ReadArray(root, "runAttempts")
            .Select(ReadRunAuthority)
            .ToArray();
        var reviews = ReadArray(root, "reviewAttempts")
            .Select(ReadReviewAuthority)
            .ToArray();

        var duplicateAttempt = runs.Select(run => run.AttemptId)
            .Concat(reviews.Select(review => review.AttemptId))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAttempt is not null)
            throw new InvalidDataException($"Legacy attempt authority contains duplicate id '{duplicateAttempt.Key}'.");

        return new LegacyAuthorityImport(epoch, fences, runs, reviews);
    }

    private static LegacyRunAuthorityImport ReadRunAuthority(JsonElement value)
        => new(
            RequiredString(value, "attemptId"),
            RequiredString(value, "taskKey").ToUpperInvariant(),
            ReadString(value, "repositoryId") ?? string.Empty,
            ReadAttemptState(value),
            ReadInt64(value, "lastFence", 0),
            ReadInt64(value, "authorityEpoch", 1),
            ReadDate(value, "createdAt") ?? throw new InvalidDataException("Legacy coding attempt has no createdAt."),
            ReadDate(value, "terminalAt"),
            ReadString(value, "resultSha"),
            ReadString(value, "terminalOutcome"),
            ReadString(value, "terminalReason"),
            ReadLease(value));

    private static LegacyReviewAuthorityImport ReadReviewAuthority(JsonElement value)
    {
        if (!value.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Legacy review attempt has no subject authority.");
        var sourceRunAttemptId = RequiredString(value, "sourceRunAttemptId");
        var subjectSourceRunAttemptId = RequiredString(subject, "sourceRunAttemptId");
        if (!string.Equals(sourceRunAttemptId, subjectSourceRunAttemptId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Legacy review attempt and subject reference different coding attempts.");
        return new LegacyReviewAuthorityImport(
            RequiredString(value, "attemptId"),
            RequiredString(value, "taskKey").ToUpperInvariant(),
            ReadString(value, "repositoryId") ?? string.Empty,
            sourceRunAttemptId,
            ReadAttemptState(value),
            ReadInt64(value, "lastFence", 0),
            ReadInt64(value, "authorityEpoch", 1),
            ReadDate(value, "createdAt") ?? throw new InvalidDataException("Legacy review attempt has no createdAt."),
            ReadDate(value, "terminalAt"),
            ReadOptionalEnum(value, "outcome"),
            ReadString(value, "failureClassification"),
            ReadString(value, "terminalReason"),
            ReadLease(value),
            new LegacyReviewSubjectImport(
                RequiredString(subject, "subjectId"),
                ReadString(subject, "repositoryId") ?? string.Empty,
                ReadString(subject, "repositoryUrl"),
                ReadString(subject, "expectedResultSha") ?? string.Empty,
                subjectSourceRunAttemptId,
                ReadString(subject, "reviewPolicyHash") ?? "legacy",
                subject.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object
                    ? plan.GetRawText()
                    : "{\"commands\":[],\"requiredAspects\":[]}",
                ReadDate(subject, "createdAt") ?? ReadDate(value, "createdAt") ?? DateTime.UnixEpoch,
                ReadString(subject, "resultRef")));
    }

    private static LegacyLeaseAuthorityImport? ReadLease(JsonElement value)
    {
        if (!value.TryGetProperty("lease", out var lease) || lease.ValueKind != JsonValueKind.Object)
            return null;
        return new LegacyLeaseAuthorityImport(
            RequiredString(lease, "leaseId"),
            ReadInt64(lease, "fence", 0),
            ReadInt64(lease, "authorityEpoch", 1),
            RequiredString(lease, "executorId"),
            RequiredString(lease, "hostId"),
            ReadString(lease, "leaseInstanceId") ?? RequiredString(lease, "leaseId"),
            ReadDate(lease, "acquiredAt") ?? throw new InvalidDataException("Legacy lease has no acquiredAt."),
            ReadDate(lease, "expiresAt") ?? throw new InvalidDataException("Legacy lease has no expiresAt."));
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static IReadOnlyDictionary<string, long> ReadLongDictionary(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(item => item.Name.ToUpperInvariant(), item => item.Value.GetInt64(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private static long ReadInt64(JsonElement element, string property, long fallback)
        => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : fallback;

    private static string RequiredString(JsonElement element, string property)
        => ReadString(element, property) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Legacy authority property '{property}' is required.");

    private static string ReadAttemptState(JsonElement element)
    {
        if (!element.TryGetProperty("state", out var value))
            throw new InvalidDataException("Legacy authority property 'state' is required.");
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
            return numeric switch
            {
                0 => "pending",
                1 => "leased",
                2 => "completed",
                3 => "failed",
                4 => "cancelled",
                5 => "superseded",
                _ => throw new InvalidDataException($"Unknown legacy attempt state value '{numeric}'."),
            };
        throw new InvalidDataException("Legacy authority property 'state' is invalid.");
    }

    private static string? ReadOptionalEnum(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
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
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
        LegacyAuthorityImport Authority);
}
