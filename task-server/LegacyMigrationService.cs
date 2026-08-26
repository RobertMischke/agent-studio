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
            scan.Authority.RunnerIdentities.Count,
            scan.Authority.RunAttempts.Count,
            scan.Authority.ActiveLeaseCount,
            scan.Authority.ReviewAttempts.Count);
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
        await store.ImportLegacyBatchAsync(
            request.WorkspaceName, scan.Projects, scan.Authority, actorId, ct);

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
            scan.Authority.RunnerIdentities.Count,
            scan.Authority.RunAttempts.Count,
            scan.Authority.ActiveLeaseCount,
            scan.Authority.ReviewAttempts.Count);
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
            .Select(group => group.OrderBy(path =>
                    string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .First())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var byProject = new Dictionary<string, List<LegacyTaskImport>>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new HashSet<string>(taskFiles, StringComparer.Ordinal);
        var authorityPath = Path.Combine(root, ".metadata", "attempt-authority.json");
        AddIfPresent(sourceFiles, authorityPath);
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
        var authority = await ReadAttemptAuthorityAsync(authorityPath, ct);
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
        return new LegacyScan(
            migrationId, projects, eventCount, artifactCount, evidenceGitRoots,
            warnings, authority);
    }

    private static async Task<LegacyAttemptAuthorityImport> ReadAttemptAuthorityAsync(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return LegacyAttemptAuthorityImport.Empty;

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        var runs = ReadArray(root, "runAttempts")
            .Select(ReadRunAttempt)
            .ToArray();
        var reviews = ReadArray(root, "reviewAttempts")
            .Select(ReadReviewAttempt)
            .ToArray();
        var runners = runs.Select(run => run.Lease)
            .Concat(reviews.Select(review => review.Lease))
            .Where(lease => lease is not null)
            .Select(lease => lease!)
            .GroupBy(lease => lease.ExecutorId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LegacyRunnerIdentityImport(
                group.Key,
                group.Select(item => item.ExecutorDisplayName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? group.Key,
                group.Select(item => item.HostId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "legacy-host",
                group.Select(item => item.InstanceId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "legacy-instance"))
            .ToArray();
        var counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("lastFenceByTask", out var fenceElement)
            && fenceElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in fenceElement.EnumerateObject())
                if (property.Value.TryGetInt64(out var fence)) counters[property.Name] = fence;
        }
        foreach (var attempt in runs)
            counters[attempt.TaskKey] = Math.Max(
                counters.GetValueOrDefault(attempt.TaskKey), attempt.LastFence);

        return new LegacyAttemptAuthorityImport(runners, runs, reviews, counters);
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static LegacyRunAttemptImport ReadRunAttempt(JsonElement value)
        => new(
            RequiredString(value, "attemptId"),
            RequiredString(value, "taskKey").Trim().ToUpperInvariant(),
            ReadString(value, "repositoryId") ?? string.Empty,
            ReadString(value, "state") ?? "Pending",
            value.TryGetProperty("lastFence", out var fence) && fence.TryGetInt64(out var number) ? number : 0,
            ReadDate(value, "createdAt") ?? DateTime.UnixEpoch,
            ReadDate(value, "terminalAt"),
            ReadString(value, "resultSha"),
            ReadString(value, "terminalOutcome"),
            value.TryGetProperty("lease", out var lease) && lease.ValueKind == JsonValueKind.Object
                ? ReadLease(lease)
                : null);

    private static LegacyReviewAttemptImport ReadReviewAttempt(JsonElement value)
    {
        var subject = value.TryGetProperty("subject", out var subjectValue)
                      && subjectValue.ValueKind == JsonValueKind.Object
            ? subjectValue
            : default;
        return new LegacyReviewAttemptImport(
            RequiredString(value, "attemptId"),
            RequiredString(value, "taskKey").Trim().ToUpperInvariant(),
            ReadString(value, "repositoryId") ?? string.Empty,
            RequiredString(value, "sourceRunAttemptId"),
            subject.ValueKind == JsonValueKind.Object
                ? RequiredString(subject, "subjectId")
                : RequiredString(value, "attemptId") + "-subject",
            subject.ValueKind == JsonValueKind.Object
                ? ReadString(subject, "expectedResultSha") ?? string.Empty
                : string.Empty,
            subject.ValueKind == JsonValueKind.Object
                ? ReadString(subject, "reviewPolicyHash") ?? "legacy"
                : "legacy",
            subject.ValueKind == JsonValueKind.Object
                ? ReadString(subject, "repositoryUrl")
                : null,
            subject.ValueKind == JsonValueKind.Object
                ? ReadString(subject, "resultRef")
                : null,
            subject.ValueKind == JsonValueKind.Object
                ? ReadDate(subject, "createdAt") ?? DateTime.UnixEpoch
                : ReadDate(value, "createdAt") ?? DateTime.UnixEpoch,
            ReadString(value, "state") ?? "Pending",
            value.TryGetProperty("lastFence", out var fence) && fence.TryGetInt64(out var number) ? number : 0,
            ReadDate(value, "createdAt") ?? DateTime.UnixEpoch,
            ReadDate(value, "terminalAt"),
            ReadString(value, "outcome"),
            ReadString(value, "failureClassification"),
            value.TryGetProperty("lease", out var lease) && lease.ValueKind == JsonValueKind.Object
                ? ReadLease(lease)
                : null);
    }

    private static LegacyAttemptLeaseImport ReadLease(JsonElement value)
        => new(
            RequiredString(value, "leaseId"),
            ReadString(value, "executorId") ?? "legacy-runner",
            ReadString(value, "hostId") ?? "legacy-host",
            ReadString(value, "leaseInstanceId") ?? ReadString(value, "clientId") ?? "legacy-instance",
            ReadDate(value, "acquiredAt") ?? DateTime.UnixEpoch,
            ReadDate(value, "expiresAt") ?? DateTime.UnixEpoch,
            ReadString(value, "executorDisplayName"),
            ReadString(value, "clientId"));

    private static string RequiredString(JsonElement value, string propertyName)
        => ReadString(value, propertyName)
           ?? throw new InvalidDataException(
               $"Legacy attempt authority is missing required property '{propertyName}'.");

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
        LegacyAttemptAuthorityImport Authority);
}
