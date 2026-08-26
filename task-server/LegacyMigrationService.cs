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
            scan.Authority.Runners.Count,
            scan.Authority.Runs.Count,
            scan.Authority.Runs.Count(run => run.Lease is not null)
                + scan.Authority.Reviews.Count(review => review.Lease is not null),
            scan.Authority.Reviews.Count,
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
        await store.ImportLegacyBatchAsync(scan.MigrationId, request.WorkspaceName, scan.Projects, scan.Authority, actorId, ct);

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
            scan.Authority.Runners.Count,
            scan.Authority.Runs.Count,
            scan.Authority.Runs.Count(run => run.Lease is not null)
                + scan.Authority.Reviews.Count(review => review.Lease is not null),
            scan.Authority.Reviews.Count,
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
            .Distinct(StringComparer.Ordinal)
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
                var taskKey = ReadString(rootElement, "key")
                    ?? ReadString(rootElement, "taskKey")
                    ?? ReadString(rootElement, "id")
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
        var authority = await ReadAuthorityAsync(root, sourceFiles, warnings, ct);
        var authorityPath = Path.Combine(root, ".metadata", "attempt-authority.json");
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

    private static async Task<LegacyAuthorityImport> ReadAuthorityAsync(
        string root,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var path = Path.Combine(root, ".metadata", "attempt-authority.json");
        if (!File.Exists(path))
            return LegacyAuthorityImport.Empty with
            {
                Runners = await ReadRunnerIdentitiesAsync(root, [], [], sourceFiles, warnings, ct),
            };

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var json = document.RootElement;
        var epoch = ReadLong(json, "authorityEpoch");
        var runs = new List<LegacyRunImport>();
        if (json.TryGetProperty("runAttempts", out var runAttempts) && runAttempts.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runAttempts.EnumerateArray())
            {
                var attemptId = RequiredString(run, "attemptId", path);
                var taskKey = RequiredString(run, "taskKey", path).ToUpperInvariant();
                runs.Add(new LegacyRunImport(
                    attemptId,
                    taskKey,
                    ReadString(run, "repositoryId") ?? "legacy-unknown",
                    ReadAttemptState(run),
                    ReadString(run, "resultSha"),
                    ReadLong(run, "lastFence"),
                    ReadLong(run, "authorityEpoch"),
                    ReadDate(run, "createdAt") ?? File.GetLastWriteTimeUtc(path),
                    ReadDate(run, "terminalAt"),
                    run.TryGetProperty("lease", out var lease) && lease.ValueKind == JsonValueKind.Object
                        ? new LegacyLeaseImport(
                            RequiredString(lease, "leaseId", path),
                            ReadString(lease, "executorId") ?? "legacy-runner",
                            ReadString(lease, "hostId") ?? "legacy-host",
                            ReadString(lease, "leaseInstanceId") ?? ReadString(lease, "clientId") ?? "legacy-instance",
                            ReadLong(lease, "fence"),
                            ReadLong(lease, "authorityEpoch"),
                            ReadDate(lease, "acquiredAt") ?? File.GetLastWriteTimeUtc(path),
                            ReadDate(lease, "expiresAt") ?? File.GetLastWriteTimeUtc(path))
                        : null));
            }
        }

        var reviews = new List<LegacyReviewImport>();
        if (json.TryGetProperty("reviewAttempts", out var reviewAttempts) && reviewAttempts.ValueKind == JsonValueKind.Array)
        {
            foreach (var review in reviewAttempts.EnumerateArray())
            {
                var subject = review.TryGetProperty("subject", out var subjectValue)
                    && subjectValue.ValueKind == JsonValueKind.Object
                        ? subjectValue
                        : throw new InvalidDataException($"Review authority in '{path}' has no immutable subject.");
                reviews.Add(new LegacyReviewImport(
                    RequiredString(review, "attemptId", path),
                    RequiredString(review, "taskKey", path).ToUpperInvariant(),
                    ReadString(review, "repositoryId") ?? RequiredString(subject, "repositoryId", path),
                    RequiredString(review, "sourceRunAttemptId", path),
                    RequiredString(subject, "subjectId", path),
                    RequiredString(subject, "expectedResultSha", path),
                    ReadString(subject, "repositoryUrl"),
                    ReadString(subject, "resultRef"),
                    ReadString(subject, "reviewPolicyHash") ?? "legacy-policy",
                    subject.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object
                        ? plan.GetRawText()
                        : "{\"commands\":[],\"requiredAspects\":[]}",
                    ReadAttemptState(review),
                    ReadLong(review, "lastFence"),
                    ReadLong(review, "authorityEpoch"),
                    ReadDate(review, "createdAt") ?? File.GetLastWriteTimeUtc(path),
                    ReadDate(review, "terminalAt"),
                    review.TryGetProperty("lease", out var lease) && lease.ValueKind == JsonValueKind.Object
                        ? new LegacyLeaseImport(
                            RequiredString(lease, "leaseId", path),
                            ReadString(lease, "executorId") ?? "legacy-review-runner",
                            ReadString(lease, "hostId") ?? "legacy-review-host",
                            ReadString(lease, "leaseInstanceId") ?? ReadString(lease, "clientId") ?? "legacy-review-instance",
                            ReadLong(lease, "fence"),
                            ReadLong(lease, "authorityEpoch"),
                            ReadDate(lease, "acquiredAt") ?? File.GetLastWriteTimeUtc(path),
                            ReadDate(lease, "expiresAt") ?? File.GetLastWriteTimeUtc(path))
                        : null));
            }
        }
        var fences = new List<LegacyTaskFenceImport>();
        if (json.TryGetProperty("lastFenceByTask", out var fenceObject)
            && fenceObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var fence in fenceObject.EnumerateObject())
            {
                if (!fence.Value.TryGetInt64(out var lastFence) || lastFence < 0)
                    throw new InvalidDataException($"Legacy fence history for '{fence.Name}' is invalid.");
                fences.Add(new LegacyTaskFenceImport(fence.Name.Trim().ToUpperInvariant(), lastFence));
            }
        }
        var runners = await ReadRunnerIdentitiesAsync(root, runs, reviews, sourceFiles, warnings, ct);
        return new LegacyAuthorityImport(epoch, runners, runs, reviews, fences);
    }

    private static async Task<IReadOnlyList<LegacyRunnerImport>> ReadRunnerIdentitiesAsync(
        string root,
        IReadOnlyList<LegacyRunImport> runs,
        IReadOnlyList<LegacyReviewImport> reviews,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var identities = new Dictionary<string, LegacyRunnerIdentity>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(root, "identities");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                sourceFiles.Add(path);
                try
                {
                    await using var stream = File.OpenRead(path);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var item = document.RootElement;
                    var id = ReadString(item, "id")?.Trim();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        warnings.Add($"Skipped identity '{Path.GetFileName(path)}' because it has no id.");
                        continue;
                    }
                    var kind = ReadIdentityKind(item);
                    var runnerState = ReadString(item, "runnerDaemonState");
                    var desired = ReadInt(item, "runnerDesiredMaxParallelism");
                    var effective = ReadInt(item, "runnerEffectiveMaxParallelism");
                    if (kind is not ("agent-instance" or "agentinstance" or "service" or "retired")
                        && string.IsNullOrWhiteSpace(runnerState)
                        && desired is null
                        && effective is null)
                        continue;
                    identities[id] = new LegacyRunnerIdentity(
                        id,
                        ReadString(item, "displayName") ?? id,
                        kind,
                        ReadDate(item, "registeredAt") ?? File.GetCreationTimeUtc(path),
                        ReadDate(item, "lastSeenAt") ?? File.GetLastWriteTimeUtc(path),
                        desired,
                        effective);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    warnings.Add($"Skipped unreadable identity '{Path.GetFileName(path)}': {exception.Message}");
                }
            }
        }

        var roles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var leases = runs.Where(run => run.Lease is not null)
            .Select(run => (Role: ReviewCapabilities.CodingExecutor, Lease: run.Lease!))
            .Concat(reviews.Where(review => review.Lease is not null)
                .Select(review => (Role: ReviewCapabilities.ReviewExecutor, Lease: review.Lease!)));
        foreach (var entry in leases)
        {
            if (!roles.TryGetValue(entry.Lease.RunnerId, out var runnerRoles))
                roles[entry.Lease.RunnerId] = runnerRoles = new HashSet<string>(StringComparer.Ordinal);
            runnerRoles.Add(entry.Role);
        }
        foreach (var pair in roles.Where(pair => pair.Value.Count > 1))
            throw new InvalidDataException(
                $"Legacy executor '{pair.Key}' holds both coding and review authority. Split the identities before cutover.");

        var runnerIds = identities.Keys.Concat(roles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return runnerIds.Select(id =>
        {
            identities.TryGetValue(id, out var identity);
            var matchingLeases = runs.Select(run => run.Lease)
                .Concat(reviews.Select(review => review.Lease))
                .Where(lease => lease is not null && string.Equals(lease.RunnerId, id, StringComparison.OrdinalIgnoreCase))
                .Cast<LegacyLeaseImport>()
                .OrderByDescending(lease => lease.AcquiredAt)
                .ToArray();
            var lease = matchingLeases.FirstOrDefault();
            var role = roles.TryGetValue(id, out var runnerRoles) ? runnerRoles.Single() : null;
            return new LegacyRunnerImport(
                id,
                identity?.DisplayName ?? id,
                lease?.HostId ?? id,
                lease?.InstanceId ?? $"legacy-{id}",
                role,
                identity?.Kind == "retired" ? "retired" : "active",
                identity?.RegisteredAt ?? lease?.AcquiredAt ?? DateTime.UnixEpoch,
                identity?.LastSeenAt ?? lease?.AcquiredAt ?? DateTime.UnixEpoch,
                identity?.DesiredMaxParallelism,
                identity?.EffectiveMaxParallelism);
        }).ToArray();
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

    private static long ReadLong(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static string ReadIdentityKind(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var value)) return "human";
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString() ?? "human").Replace("_", "-").ToLowerInvariant();
        if (value.TryGetInt32(out var number) && number is >= 0 and <= 4)
            return new[] { "human", "agent-instance", "external-tool", "service", "retired" }[number];
        return "human";
    }

    private static string ReadAttemptState(JsonElement element)
    {
        if (!element.TryGetProperty("state", out var value)) return "Pending";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "Pending";
        if (value.TryGetInt32(out var number) && number is >= 0 and <= 5)
            return new[] { "Pending", "Leased", "Completed", "Failed", "Cancelled", "Superseded" }[number];
        throw new InvalidDataException($"Legacy attempt authority contains unknown state '{value.GetRawText()}'.");
    }

    private static string RequiredString(JsonElement element, string property, string path)
        => ReadString(element, property) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Legacy authority '{path}' is missing required '{property}'.");

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

    private sealed record LegacyRunnerIdentity(
        string Id,
        string DisplayName,
        string Kind,
        DateTime RegisteredAt,
        DateTime LastSeenAt,
        int? DesiredMaxParallelism,
        int? EffectiveMaxParallelism);
}
