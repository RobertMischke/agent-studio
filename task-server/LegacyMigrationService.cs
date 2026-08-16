using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class LegacyMigrationService(TaskServerStore store)
{
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

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
            scan.Runners.Count,
            scan.Runs.Count,
            scan.Runs.Count(run => run.Lease is not null)
                + scan.ReviewAttempts.Count(review => review.Lease is not null),
            scan.ReviewAttempts.Count);
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
            scan.MigrationId,
            request.WorkspaceName,
            scan.Projects,
            scan.Runners,
            scan.Runs,
            scan.ReviewAttempts,
            scan.Fences,
            actorId,
            ct);

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
            scan.Runners.Count,
            scan.Runs.Count,
            scan.Runs.Count(run => run.Lease is not null)
                + scan.ReviewAttempts.Count(review => review.Lease is not null),
            scan.ReviewAttempts.Count);
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
        var taskIds = projects
            .SelectMany(project => project.Tasks)
            .ToDictionary(task => task.TaskKey, task => task.TaskId, StringComparer.OrdinalIgnoreCase);
        var authority = await ReadAuthorityAsync(root, taskIds, sourceFiles, warnings, ct);
        var runners = await ReadRunnerIdentitiesAsync(
            root,
            authority.Runs,
            authority.ReviewAttempts,
            sourceFiles,
            warnings,
            ct);
        var evidenceGitRoots = FindEvidenceGitRoots(root);
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
            migrationId,
            projects,
            runners,
            authority.Runs,
            authority.ReviewAttempts,
            authority.Fences,
            eventCount,
            artifactCount,
            evidenceGitRoots,
            warnings);
    }

    private static async Task<LegacyAuthorityScan> ReadAuthorityAsync(
        string root,
        IReadOnlyDictionary<string, string> taskIds,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var path = Path.Combine(root, ".metadata", "attempt-authority.json");
        if (!File.Exists(path)) return new LegacyAuthorityScan([], [], []);
        sourceFiles.Add(path);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var rootElement = document.RootElement;
        var runs = new List<LegacyRunAuthorityImport>();
        if (TryGetProperty(rootElement, "runAttempts", out var runAttempts)
            && runAttempts.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in runAttempts.EnumerateArray())
            {
                var taskKey = ReadString(item, "taskKey")?.Trim().ToUpperInvariant() ?? string.Empty;
                var state = ReadLifecycleState(item, "state");
                if (!taskIds.TryGetValue(taskKey, out var taskId))
                {
                    var message = $"Attempt authority references missing task '{taskKey}'.";
                    if (state == LegacyAttemptState.Leased)
                        throw new InvalidDataException(message + " Active authority cannot be skipped safely.");
                    warnings.Add(message + " Terminal history was skipped.");
                    continue;
                }
                var attemptId = RequiredString(item, "attemptId", "RunAttempt id");
                var lease = ReadLease(item);
                if (state == LegacyAttemptState.Leased && lease is null)
                    throw new InvalidDataException(
                        $"Active RunAttempt '{attemptId}' has no lease. Authority cannot be migrated safely.");
                runs.Add(new LegacyRunAuthorityImport(
                    attemptId,
                    taskId,
                    taskKey,
                    RequiredString(item, "repositoryId", $"RunAttempt '{attemptId}' repository"),
                    state,
                    Math.Max(ReadLong(item, "lastFence"), lease?.Fence ?? 0),
                    ReadDate(item, "createdAt") ?? File.GetLastWriteTimeUtc(path),
                    ReadDate(item, "terminalAt"),
                    ReadString(item, "resultSha"),
                    ReadString(item, "terminalOutcome"),
                    ReadString(item, "terminalReason"),
                    DeserializeOptional<ImmutableResultEnvelope>(item, "resultEnvelope"),
                    ReadString(item, "resultEnvelopeDigest"),
                    lease));
            }
        }

        var reviews = new List<LegacyReviewAuthorityImport>();
        if (TryGetProperty(rootElement, "reviewAttempts", out var reviewAttempts)
            && reviewAttempts.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reviewAttempts.EnumerateArray())
            {
                var taskKey = ReadString(item, "taskKey")?.Trim().ToUpperInvariant() ?? string.Empty;
                var state = ReadLifecycleState(item, "state");
                if (!taskIds.TryGetValue(taskKey, out var taskId))
                {
                    var message = $"Review authority references missing task '{taskKey}'.";
                    if (state == LegacyAttemptState.Leased)
                        throw new InvalidDataException(message + " Active authority cannot be skipped safely.");
                    warnings.Add(message + " Terminal history was skipped.");
                    continue;
                }
                var attemptId = RequiredString(item, "attemptId", "ReviewAttempt id");
                if (!TryGetProperty(item, "subject", out var subject)
                    || subject.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"ReviewAttempt '{attemptId}' has no immutable subject.");
                var lease = ReadLease(item);
                if (state == LegacyAttemptState.Leased && lease is null)
                    throw new InvalidDataException(
                        $"Active ReviewAttempt '{attemptId}' has no lease. Authority cannot be migrated safely.");
                var sourceRunAttemptId = RequiredString(
                    item,
                    "sourceRunAttemptId",
                    $"ReviewAttempt '{attemptId}' source run");
                var subjectId = RequiredString(subject, "subjectId", $"ReviewAttempt '{attemptId}' subject id");
                reviews.Add(new LegacyReviewAuthorityImport(
                    attemptId,
                    subjectId,
                    taskId,
                    taskKey,
                    sourceRunAttemptId,
                    RequiredString(subject, "repositoryId", $"Review subject '{subjectId}' repository"),
                    ReadString(subject, "repositoryUrl"),
                    RequiredString(subject, "expectedResultSha", $"Review subject '{subjectId}' result SHA"),
                    ReadString(subject, "resultRef"),
                    ReadString(subject, "reviewPolicyHash") ?? "legacy",
                    DeserializeOptional<ReviewPlanDto>(subject, "plan")
                        ?? new ReviewPlanDto([], []),
                    ReadDate(subject, "createdAt") ?? ReadDate(item, "createdAt") ?? File.GetLastWriteTimeUtc(path),
                    state,
                    Math.Max(ReadLong(item, "lastFence"), lease?.Fence ?? 0),
                    ReadDate(item, "createdAt") ?? File.GetLastWriteTimeUtc(path),
                    ReadDate(item, "terminalAt"),
                    ReadEnumText(item, "outcome"),
                    ReadString(item, "failureClassification"),
                    ReadString(item, "terminalReason"),
                    lease));
            }
        }

        var fences = new List<LegacyTaskFenceImport>();
        if (TryGetProperty(rootElement, "lastFenceByTask", out var fenceObject)
            && fenceObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var fence in fenceObject.EnumerateObject())
            {
                var taskKey = fence.Name.Trim().ToUpperInvariant();
                if (!taskIds.TryGetValue(taskKey, out var taskId))
                {
                    warnings.Add($"Fence history references missing task '{taskKey}' and was skipped.");
                    continue;
                }
                if (!fence.Value.TryGetInt64(out var lastFence) || lastFence < 0)
                    throw new InvalidDataException($"Fence history for task '{taskKey}' is invalid.");
                fences.Add(new LegacyTaskFenceImport(taskId, lastFence));
            }
        }

        var knownRuns = runs.Select(run => run.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews)
            if (!knownRuns.Contains(review.SourceRunId))
                throw new InvalidDataException(
                    $"ReviewAttempt '{review.AttemptId}' references RunAttempt '{review.SourceRunId}', which is absent from the live authority store.");
        return new LegacyAuthorityScan(runs, reviews, fences);
    }

    private static async Task<IReadOnlyList<LegacyRunnerImport>> ReadRunnerIdentitiesAsync(
        string root,
        IReadOnlyList<LegacyRunAuthorityImport> runs,
        IReadOnlyList<LegacyReviewAuthorityImport> reviews,
        ISet<string> sourceFiles,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var identities = new Dictionary<string, LegacyIdentity>(StringComparer.OrdinalIgnoreCase);
        var identitiesDirectory = Path.Combine(root, "identities");
        if (Directory.Exists(identitiesDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(identitiesDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                sourceFiles.Add(path);
                try
                {
                    await using var stream = File.OpenRead(path);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var json = document.RootElement;
                    var id = ReadString(json, "id")?.Trim();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        warnings.Add($"Skipped identity '{Path.GetFileName(path)}' because it has no id.");
                        continue;
                    }
                    identities[id] = new LegacyIdentity(
                        id,
                        ReadString(json, "displayName") ?? id,
                        ReadIdentityKind(json),
                        ReadDate(json, "registeredAt") ?? File.GetCreationTimeUtc(path),
                        ReadDate(json, "lastSeenAt") ?? File.GetLastWriteTimeUtc(path),
                        ReadString(json, "runnerDaemonState"),
                        ReadInt(json, "runnerDesiredMaxParallelism"),
                        ReadInt(json, "runnerEffectiveMaxParallelism"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    warnings.Add($"Skipped unreadable identity '{Path.GetFileName(path)}': {exception.Message}");
                }
            }
        }

        var roles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var leases = runs
            .Where(run => run.Lease is not null)
            .Select(run => (Role: ReviewCapabilities.CodingExecutor, Lease: run.Lease!))
            .Concat(reviews
                .Where(review => review.Lease is not null)
                .Select(review => (Role: ReviewCapabilities.ReviewExecutor, Lease: review.Lease!)))
            .ToList();
        foreach (var entry in leases)
        {
            if (!roles.TryGetValue(entry.Lease.ExecutorId, out var runnerRoles))
                roles[entry.Lease.ExecutorId] = runnerRoles = new HashSet<string>(StringComparer.Ordinal);
            runnerRoles.Add(entry.Role);
        }
        foreach (var pair in roles.Where(pair => pair.Value.Count > 1))
            throw new InvalidDataException(
                $"Legacy executor '{pair.Key}' holds both coding and review authority. Split the identities before cutover.");

        var runnerIds = identities.Values
            .Where(identity => IsRunnerIdentity(identity) || roles.ContainsKey(identity.Id))
            .Select(identity => identity.Id)
            .Concat(roles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var result = new List<LegacyRunnerImport>(runnerIds.Length);
        foreach (var runnerId in runnerIds)
        {
            identities.TryGetValue(runnerId, out var identity);
            var runnerLeases = leases
                .Where(entry => string.Equals(entry.Lease.ExecutorId, runnerId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Lease)
                .OrderByDescending(lease => lease.AcquiredAt)
                .ToArray();
            var currentLease = runnerLeases.FirstOrDefault();
            var role = roles.TryGetValue(runnerId, out var runnerRoles)
                ? runnerRoles.Single()
                : string.Empty;
            var registeredAt = identity?.RegisteredAt
                ?? currentLease?.AcquiredAt
                ?? DateTime.UnixEpoch;
            var lastSeenAt = identity?.LastSeenAt
                ?? currentLease?.LastHeartbeat
                ?? registeredAt;
            result.Add(new LegacyRunnerImport(
                runnerId,
                identity?.DisplayName ?? runnerId,
                currentLease?.HostId ?? runnerId,
                currentLease?.LeaseInstanceId ?? $"legacy-{runnerId}",
                role,
                string.Equals(identity?.Kind, "retired", StringComparison.OrdinalIgnoreCase)
                    ? "retired"
                    : "active",
                registeredAt,
                lastSeenAt,
                identity?.DesiredMaxParallelism,
                identity?.EffectiveMaxParallelism));
        }
        return result;
    }

    private static bool IsRunnerIdentity(LegacyIdentity identity)
        => identity.Kind is "agentinstance" or "agent-instance" or "service" or "retired"
           || !string.IsNullOrWhiteSpace(identity.RunnerDaemonState)
           || identity.DesiredMaxParallelism is not null
           || identity.EffectiveMaxParallelism is not null;

    private static LegacyLeaseImport? ReadLease(JsonElement item)
    {
        if (!TryGetProperty(item, "lease", out var lease)
            || lease.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var leaseId = RequiredString(lease, "leaseId", "Attempt lease id");
        return new LegacyLeaseImport(
            leaseId,
            RequiredString(lease, "executorId", $"Lease '{leaseId}' executor"),
            RequiredString(lease, "hostId", $"Lease '{leaseId}' host"),
            ReadString(lease, "leaseInstanceId") ?? $"legacy-{leaseId}",
            Math.Max(1, ReadLong(lease, "fence")),
            ReadDate(lease, "acquiredAt") ?? DateTime.UnixEpoch,
            ReadDate(lease, "expiresAt") ?? DateTime.UnixEpoch,
            ReadDate(lease, "lastHeartbeat") ?? DateTime.UnixEpoch);
    }

    private static LegacyAttemptState ReadLifecycleState(JsonElement item, string property)
    {
        if (!TryGetProperty(item, property, out var value)) return LegacyAttemptState.Pending;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            && Enum.IsDefined(typeof(LegacyAttemptState), number))
            return (LegacyAttemptState)number;
        if (value.ValueKind == JsonValueKind.String
            && Enum.TryParse<LegacyAttemptState>(value.GetString(), true, out var parsed))
            return parsed;
        throw new InvalidDataException($"Unknown legacy attempt state '{value}'.");
    }

    private static string ReadIdentityKind(JsonElement item)
    {
        if (!TryGetProperty(item, "kind", out var value)) return "human";
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Replace("_", "-").ToLowerInvariant() ?? "human";
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number switch
            {
                1 => "agent-instance",
                2 => "external-tool",
                3 => "service",
                4 => "retired",
                _ => "human",
            };
        return "human";
    }

    private static T? DeserializeOptional<T>(JsonElement item, string property) where T : class
        => TryGetProperty(item, property, out var value)
           && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.Deserialize<T>(LegacyJson)
            : null;

    private static string RequiredString(JsonElement item, string property, string label)
        => ReadString(item, property) is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidDataException($"{label} is required.");

    private static string? ReadEnumText(JsonElement item, string property)
    {
        if (!TryGetProperty(item, property, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static long ReadLong(JsonElement item, string property)
        => TryGetProperty(item, property, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static int? ReadInt(JsonElement item, string property)
        => TryGetProperty(item, property, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static bool TryGetProperty(JsonElement item, string property, out JsonElement value)
    {
        foreach (var candidate in item.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
            value = candidate.Value;
            return true;
        }
        value = default;
        return false;
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
        => TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
        IReadOnlyList<LegacyRunnerImport> Runners,
        IReadOnlyList<LegacyRunAuthorityImport> Runs,
        IReadOnlyList<LegacyReviewAuthorityImport> ReviewAttempts,
        IReadOnlyList<LegacyTaskFenceImport> Fences,
        int EventCount,
        int ArtifactCount,
        IReadOnlyList<string> EvidenceGitRoots,
        IReadOnlyList<string> Warnings);

    private sealed record LegacyAuthorityScan(
        IReadOnlyList<LegacyRunAuthorityImport> Runs,
        IReadOnlyList<LegacyReviewAuthorityImport> ReviewAttempts,
        IReadOnlyList<LegacyTaskFenceImport> Fences);

    private sealed record LegacyIdentity(
        string Id,
        string DisplayName,
        string Kind,
        DateTime RegisteredAt,
        DateTime LastSeenAt,
        string? RunnerDaemonState,
        int? DesiredMaxParallelism,
        int? EffectiveMaxParallelism);
}

internal enum LegacyAttemptState
{
    Pending,
    Leased,
    Completed,
    Failed,
    Cancelled,
    Superseded,
}
