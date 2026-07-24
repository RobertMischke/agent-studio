using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public const int CurrentSchemaVersion = 2;
    private const string TimestampFormat = "O";
    private readonly TaskServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DateTime _startedAt;
    private string _serverId = string.Empty;
    private TaskServerMode _mode = TaskServerMode.Maintenance;

    public TaskServerStore(IOptions<TaskServerOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        _startedAt = UtcNow;
    }

    public string DataDirectory => _options.ResolveDataDirectory();
    public string DatabasePath => Path.Combine(DataDirectory, "task-server.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    public bool AuthorityReady { get; private set; }
    public string ServerId => _serverId;
    public TaskServerMode Mode => _mode;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AuthorityReady = false;
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = Open();
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            await ApplyMigrationsAsync(connection, cancellationToken);
            _serverId = await GetOrCreateMetaAsync(connection, "server_id", $"srv_{Guid.NewGuid():N}", cancellationToken);
            var storedMode = await GetOrCreateMetaAsync(connection, "mode", TaskServerMode.Normal.ToString(), cancellationToken);
            _mode = Enum.TryParse<TaskServerMode>(storedMode, true, out var parsedMode)
                ? parsedMode
                : TaskServerMode.Maintenance;

            // A server restart cannot infer that an old runner process stopped.
            // Preserve its fence and fail the attempt closed until an explicit,
            // audited recovery releases it.
            await ExecuteAsync(connection, """
                UPDATE leases
                   SET status = 'process-unknown'
                 WHERE status = 'active';
                UPDATE runs
                   SET status = 'process-unknown'
                 WHERE status = 'running';
                UPDATE review_attempts
                   SET status = 'process-unknown'
                 WHERE status = 'leased';
                """, cancellationToken);

            var integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken), CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Task Server store integrity check failed: {integrity}");

            AuthorityReady = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public TaskServerStatusDto Status()
    {
        var version = typeof(TaskServerStore).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return new TaskServerStatusDto(
            _serverId,
            version,
            CurrentSchemaVersion,
            _mode,
            AuthorityReady,
            DataDirectory,
            new ProtocolRangeDto(
                TaskServerProtocol.Current,
                TaskServerProtocol.MinimumSupported,
                TaskServerProtocol.MaximumSupported,
                version,
                _serverId,
                ["studio", "runner", "management"]),
            _startedAt);
    }

    public async Task<WorkspaceDto> CreateWorkspaceAsync(CreateWorkspaceRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Workspace name is required.");
        var id = StableOrGeneratedId(request.WorkspaceId, "wsp");
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO workspaces(id, name, version, created_at, updated_at)
                VALUES ($id, $name, 1, $now, $now);
                """, ct, transaction, ("$id", id), ("$name", request.Name.Trim()), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "workspace.created", "workspace", id,
                JsonSerializer.Serialize(new { request.Name }), ct);
        }, ct);
        return new WorkspaceDto(id, request.Name.Trim(), 1, Parse(now), Parse(now));
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, "SELECT id, name, version, created_at, updated_at FROM workspaces ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkspaceDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new WorkspaceDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Parse(reader.GetString(3)), Parse(reader.GetString(4))));
        return result;
    }

    public async Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TaskKeyPrefix))
            throw new ArgumentException("Project name and task key prefix are required.");
        var id = StableOrGeneratedId(request.ProjectId, "prj");
        var prefix = request.TaskKeyPrefix.Trim().ToUpperInvariant();
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO projects(id, workspace_id, name, task_key_prefix, next_task_number, version, created_at, updated_at)
                VALUES ($id, $workspace, $name, $prefix, 1, 1, $now, $now);
                """, ct, transaction,
                ("$id", id), ("$workspace", request.WorkspaceId), ("$name", request.Name.Trim()), ("$prefix", prefix), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "project.created", "project", id,
                JsonSerializer.Serialize(new { request.WorkspaceId, request.Name, taskKeyPrefix = prefix }), ct);
        }, ct);
        return new ProjectDto(id, request.WorkspaceId, request.Name.Trim(), prefix, 1, Parse(now), Parse(now));
    }

    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(string? workspaceId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = "SELECT id, workspace_id, name, task_key_prefix, version, created_at, updated_at FROM projects" +
                  (string.IsNullOrWhiteSpace(workspaceId) ? string.Empty : " WHERE workspace_id = $workspace") + " ORDER BY name;";
        await using var command = Command(connection, sql, ("$workspace", workspaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ProjectDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new ProjectDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), Parse(reader.GetString(5)), Parse(reader.GetString(6))));
        return result;
    }

    public async Task<TaskDto> CreateTaskAsync(string projectId, CreateTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Task title is required.");
        var taskId = StableOrGeneratedId(request.TaskId, "tsk");
        TaskDto? created = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (prefix, next) = await ReadProjectCounterAsync(connection, transaction, projectId, ct);
            var taskKey = string.IsNullOrWhiteSpace(request.TaskKey)
                ? $"{prefix}-{next}"
                : request.TaskKey.Trim().ToUpperInvariant();
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at)
                VALUES ($id, $project, $key, $title, $body, $state, 1, $now, $now);
                """, ct, transaction,
                ("$id", taskId), ("$project", projectId), ("$key", taskKey), ("$title", request.Title.Trim()),
                ("$body", request.Body), ("$state", request.State), ("$now", now));
            await ExecuteAsync(connection,
                "UPDATE projects SET next_task_number = MAX(next_task_number, $next), updated_at = $now WHERE id = $id;",
                ct, transaction, ("$next", next + 1), ("$now", now), ("$id", projectId));
            await AuditAsync(connection, transaction, actorId, "task.created", "task", taskId,
                JsonSerializer.Serialize(new { projectId, taskKey, request.State }), ct);
            created = new TaskDto(taskId, projectId, taskKey, request.Title.Trim(), request.State, 1, Parse(now), Parse(now), request.Body);
        }, ct);
        return created!;
    }

    public async Task<TaskDto?> GetTaskAsync(string projectId, string taskIdentity, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body
              FROM tasks
             WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
            """, ("$project", projectId), ("$identity", taskIdentity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<TaskDto>> ListTasksAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body
              FROM tasks WHERE project_id = $project ORDER BY created_at, task_key;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TaskDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadTask(reader));
        return result;
    }

    public async Task<TaskDto?> UpdateTaskAsync(string projectId, string taskIdentity, UpdateTaskRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        TaskDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadTaskAsync(connection, transaction, projectId, taskIdentity, ct);
            if (existing is null) return;
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException("resource-version-mismatch", $"Expected task version {request.ExpectedVersion}, current version is {existing.Version}.");
            var now = UtcNow;
            updated = existing with
            {
                Title = request.Title?.Trim() ?? existing.Title,
                Body = request.Body ?? existing.Body,
                State = request.State ?? existing.State,
                Version = existing.Version + 1,
                UpdatedAt = now,
            };
            await ExecuteAsync(connection, """
                UPDATE tasks SET title = $title, body = $body, state = $state, version = $version, updated_at = $updated
                 WHERE id = $id AND version = $expected;
                """, ct, transaction,
                ("$title", updated.Title), ("$body", updated.Body), ("$state", updated.State),
                ("$version", updated.Version), ("$updated", Iso(now)), ("$id", updated.TaskId), ("$expected", request.ExpectedVersion));
            await AuditAsync(connection, transaction, actorId, "task.updated", "task", updated.TaskId,
                JsonSerializer.Serialize(new { request.ExpectedVersion, updated.Version, updated.State }), ct);
        }, ct);
        return updated;
    }

    public async Task<RunnerDto> RegisterRunnerAsync(string runnerId, RegisterRunnerRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (!TaskServerProtocol.Supports(request.ProtocolVersion))
            throw new TaskServerProtocolException(request.ProtocolVersion);
        if (string.IsNullOrWhiteSpace(request.InstanceId) || string.IsNullOrWhiteSpace(request.HostId))
            throw new ArgumentException("Runner host and instance ids are required.");
        var capabilities = request.Capabilities ?? [];
        if (capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal)
            && capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "runner-role-conflict",
                "Coding and review executors require separate registered service identities.");

        var id = StableOrGeneratedId(runnerId, "rnr");
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existingCapabilitiesJson = Convert.ToString(
                await ScalarAsync(
                    connection,
                    "SELECT capabilities_json FROM runners WHERE id = $id;",
                    ct,
                    transaction,
                    ("$id", id)),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(existingCapabilitiesJson))
            {
                var existingCapabilities =
                    JsonSerializer.Deserialize<string[]>(existingCapabilitiesJson) ?? [];
                var changesExecutorRole =
                    existingCapabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal)
                    && capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal)
                    || existingCapabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal)
                    && capabilities.Contains(ReviewCapabilities.CodingExecutor, StringComparer.Ordinal);
                if (changesExecutorRole)
                    throw new TaskServerConflictException(
                        "runner-role-conflict",
                        "A registered coding or review service identity cannot be reused for the other executor role.");
            }
            await ExecuteAsync(connection, """
                INSERT INTO runners(id, name, host_id, instance_id, runner_version, protocol_version, capabilities_json, status, registered_at, last_seen_at)
                VALUES ($id, $name, $host, $instance, $version, $protocol, $capabilities, 'active', $now, $now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    host_id = excluded.host_id,
                    instance_id = excluded.instance_id,
                    runner_version = excluded.runner_version,
                    protocol_version = excluded.protocol_version,
                    capabilities_json = excluded.capabilities_json,
                    status = CASE WHEN runners.status = 'retired' THEN 'retired' ELSE 'active' END,
                    last_seen_at = excluded.last_seen_at;
                """, ct, transaction,
                ("$id", id), ("$name", request.Name.Trim()), ("$host", request.HostId.Trim()),
                ("$instance", request.InstanceId.Trim()), ("$version", request.RunnerVersion),
                ("$protocol", request.ProtocolVersion), ("$capabilities", JsonSerializer.Serialize(capabilities)), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "runner.registered", "runner", id,
                JsonSerializer.Serialize(new { request.HostId, request.InstanceId, request.RunnerVersion, request.ProtocolVersion }), ct);
        }, ct);
        return new RunnerDto(id, request.Name.Trim(), request.HostId.Trim(), request.InstanceId.Trim(), request.RunnerVersion,
            request.ProtocolVersion, "active", Parse(now), Parse(now));
    }

    public async Task<ClaimResponse> ClaimAsync(ClaimRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        ClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateRunnerAsync(connection, transaction, request.RunnerId, request.InstanceId, ct);
            await ExecuteAsync(connection, "UPDATE runners SET last_seen_at = $now WHERE id = $id;", ct, transaction,
                ("$now", Iso(UtcNow)), ("$id", request.RunnerId));

            if (request.AvailableSlots <= 0)
            {
                response = new ClaimResponse("empty", Message: "Runner has no available execution slot.");
                return;
            }

            TaskDto? task;
            await using (var command = Command(connection, """
                SELECT t.id, t.project_id, t.task_key, t.title, t.state, t.version, t.created_at, t.updated_at, t.body
                  FROM tasks t
                 WHERE t.state = '2-ready'
                   AND NOT EXISTS (
                       SELECT 1 FROM leases l
                        WHERE l.task_id = t.id AND l.status IN ('active', 'process-unknown'))
                 ORDER BY t.created_at, t.task_key
                 LIMIT 1;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                task = await reader.ReadAsync(ct) ? ReadTask(reader) : null;

            if (task is null)
            {
                response = new ClaimResponse("empty", Message: "No admissible task is ready.");
                return;
            }

            var fence = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction, ("$task", task.TaskId))
                ?? 0L, CultureInfo.InvariantCulture) + 1;
            await ExecuteAsync(connection, """
                INSERT INTO fence_counters(task_id, last_fence) VALUES ($task, $fence)
                ON CONFLICT(task_id) DO UPDATE SET last_fence = excluded.last_fence;
                """, ct, transaction, ("$task", task.TaskId), ("$fence", fence));

            var runId = $"run_{Guid.NewGuid():N}";
            var leaseId = $"lse_{Guid.NewGuid():N}";
            var now = UtcNow;
            var expires = now.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, """
                INSERT INTO runs(id, task_id, status, runner_id, fence, created_at, started_at)
                VALUES ($run, $task, 'running', $runner, $fence, $now, $now);
                INSERT INTO leases(task_id, lease_id, run_id, runner_id, instance_id, fence, acquired_at, expires_at, status)
                VALUES ($task, $lease, $run, $runner, $instance, $fence, $now, $expires, 'active');
                UPDATE tasks SET state = '3-progress', version = version + 1, updated_at = $now WHERE id = $task;
                """, ct, transaction,
                ("$run", runId), ("$task", task.TaskId), ("$runner", request.RunnerId), ("$instance", request.InstanceId),
                ("$fence", fence), ("$lease", leaseId), ("$now", Iso(now)), ("$expires", Iso(expires)));
            await AuditAsync(connection, transaction, actorId, "run.claimed", "run", runId,
                JsonSerializer.Serialize(new { task.TaskId, request.RunnerId, request.InstanceId, fence }), ct);

            var run = new RunDto(runId, task.TaskId, "running", request.RunnerId, fence, now, now, null);
            var lease = new LeaseDto(leaseId, runId, task.TaskId, request.RunnerId, request.InstanceId, fence, now, expires, "active");
            response = new ClaimResponse("claimed", run, task with { State = "3-progress", Version = task.Version + 1, UpdatedAt = now }, lease);
        }, ct);
        return response!;
    }

    public async Task<LeaseResponse> RenewLeaseAsync(string runId, LeaseRenewRequest request, string actorId, CancellationToken ct)
    {
        RequireAdmission();
        LeaseDto? renewed = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
                throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
            if (lease.ExpiresAt <= UtcNow)
            {
                throw new TaskServerConflictException("lease-expired-process-unknown", "Lease expired. Positive containment proof is required before recovery.");
            }
            var expires = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, "UPDATE leases SET expires_at = $expires WHERE run_id = $run;", ct, transaction,
                ("$expires", Iso(expires)), ("$run", runId));
            await ExecuteAsync(connection, "UPDATE runners SET last_seen_at = $now WHERE id = $runner;", ct, transaction,
                ("$now", Iso(UtcNow)), ("$runner", request.RunnerId));
            renewed = lease with { ExpiresAt = expires };
            await AuditAsync(connection, transaction, actorId, "lease.renewed", "run", runId,
                JsonSerializer.Serialize(new { request.Fence, expiresAt = expires }), ct);
        }, ct);
        return new LeaseResponse("renewed", renewed);
    }

    public async Task<LeaseResponse> ReleaseLeaseAsync(string runId, LeaseReleaseRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        LeaseDto? released = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
                throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'released' WHERE run_id = $run;
                UPDATE runs SET status = $outcome, finished_at = $now WHERE id = $run;
                """, ct, transaction, ("$run", runId), ("$outcome", request.Outcome), ("$now", Iso(UtcNow)));
            released = lease with { Status = "released" };
            await AuditAsync(connection, transaction, actorId, "lease.released", "run", runId,
                JsonSerializer.Serialize(new { request.Fence, request.Outcome }), ct);
        }, ct);
        return new LeaseResponse("released", released);
    }

    public async Task<RunDto> CompleteRunAsync(string runId, CompleteRunRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        RunDto? completed = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            ValidateLeaseReference(lease, request.RunnerId, request.InstanceId, request.LeaseId, request.Fence);
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'completed' WHERE run_id = $run;
                UPDATE runs
                   SET status = $outcome,
                       finished_at = $now,
                       result_sha = $resultSha,
                       repository_id = $repositoryId,
                       repository_url = $repositoryUrl,
                       result_ref = $resultRef,
                       source_bundle_artifact_id = $bundleId,
                       source_bundle_sha256 = $bundleSha
                 WHERE id = $run;
                UPDATE tasks SET state = '4-auto-review', version = version + 1, updated_at = $now WHERE id = $task;
                """, ct, transaction,
                ("$run", runId), ("$outcome", request.Outcome), ("$now", Iso(now)), ("$task", lease.TaskId),
                ("$resultSha", request.ResultSha), ("$repositoryId", request.RepositoryId),
                ("$repositoryUrl", request.RepositoryUrl), ("$resultRef", request.ResultRef),
                ("$bundleId", request.SourceBundleArtifactId), ("$bundleSha", request.SourceBundleSha256));
            await AuditAsync(connection, transaction, actorId, "run.completed", "run", runId,
                JsonSerializer.Serialize(new { request.Fence, request.Outcome, request.Summary }), ct);
            completed = new RunDto(
                runId, lease.TaskId, request.Outcome, request.RunnerId, request.Fence,
                lease.AcquiredAt, lease.AcquiredAt, now, request.ResultSha, request.RepositoryId);
        }, ct);
        return completed!;
    }

    public async Task<EventDto> IngestEventAsync(string runId, EventIngestRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        EventDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (lease.Fence != request.Fence)
                throw new TaskServerConflictException("stale-fence", "Event fence does not match the run authority.");
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
            var occurred = request.OccurredAt?.ToUniversalTime() ?? UtcNow;
            var inserted = await ExecuteAsync(connection, """
                INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                VALUES ($event, $run, $task, $kind, $payload, $key, $fence, $occurred)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """, ct, transaction,
                ("$event", request.EventId), ("$run", runId), ("$task", lease.TaskId), ("$kind", request.Kind),
                ("$payload", request.PayloadJson), ("$key", request.IdempotencyKey), ("$fence", request.Fence), ("$occurred", Iso(occurred)));
            result = await ReadEventByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            ValidateEventReplay(result, runId, lease.TaskId, request);
            if (inserted > 0)
                await AuditAsync(connection, transaction, actorId, "event.ingested", "run", runId,
                    JsonSerializer.Serialize(new { request.EventId, request.Kind, request.IdempotencyKey, request.Fence }), ct);
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<EventDto>> ListEventsAsync(string runId, long after, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at
              FROM events WHERE run_id = $run AND cursor > $after ORDER BY cursor LIMIT 1000;
            """, ("$run", runId), ("$after", after));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<EventDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadEvent(reader));
        return result;
    }

    public async Task<ArtifactDto> IngestArtifactAsync(string runId, ArtifactIngestRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        ArtifactDto? result = null;
        byte[] content;
        try { content = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("Artifact content is not valid base64."); }
        var actualSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualSha, request.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Artifact SHA-256 does not match the uploaded content.");

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (lease.Fence != request.Fence)
                throw new TaskServerConflictException("stale-fence", "Artifact fence does not match the run authority.");
            await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
            var now = UtcNow;
            var inserted = await ExecuteAsync(connection, """
                INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key, fence, created_at)
                VALUES ($id, $run, $name, $media, $sha, $content, $size, $key, $fence, $now)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """, ct, transaction,
                ("$id", request.ArtifactId), ("$run", runId), ("$name", request.Name), ("$media", request.MediaType),
                ("$sha", actualSha), ("$content", content), ("$size", content.LongLength), ("$key", request.IdempotencyKey),
                ("$fence", request.Fence), ("$now", Iso(now)));
            result = await ReadArtifactByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            ValidateArtifactReplay(result, runId, request, actualSha, content.LongLength);
            if (inserted > 0)
                await AuditAsync(connection, transaction, actorId, "artifact.ingested", "run", runId,
                    JsonSerializer.Serialize(new { request.ArtifactId, request.Name, sha256 = actualSha, request.Fence }), ct);
        }, ct);
        return result!;
    }

    public async Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(string runId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, run_id, name, media_type, sha256, size_bytes, idempotency_key, fence, created_at
              FROM artifacts WHERE run_id = $run ORDER BY created_at, id;
            """, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArtifactDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadArtifact(reader));
        return result;
    }

    public async Task<TaskServerStatusDto> ChangeModeAsync(ChangeModeRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A maintenance reason is required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await SetMetaAsync(connection, transaction, "mode", request.Mode.ToString(), ct);
            await AuditAsync(connection, transaction, actorId, "server.mode.changed", "server", _serverId,
                JsonSerializer.Serialize(new { from = _mode, to = request.Mode, request.Reason }), ct);
            _mode = request.Mode;
        }, ct, requireReady: false);
        return Status();
    }

    public async Task<PrepareShutdownResult> PrepareShutdownAsync(PrepareShutdownRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode is not TaskServerMode.Draining and not TaskServerMode.Maintenance)
            throw new TaskServerConflictException("drain-required", "Enter draining mode before safe shutdown preparation.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A shutdown reason is required.");
        PrepareShutdownResult? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var unresolved = Convert.ToInt32(await ScalarAsync(connection,
                """
                SELECT
                    (SELECT count(*) FROM leases
                      WHERE status IN ('active', 'process-unknown'))
                  + (SELECT count(*) FROM review_attempts
                      WHERE status IN ('leased', 'process-unknown'));
                """, ct, transaction) ?? 0L,
                CultureInfo.InvariantCulture);
            if (unresolved > 0)
            {
                result = new PrepareShutdownResult(false, unresolved, _mode, "Active or process-unknown attempts still hold durable authority.");
                await AuditAsync(connection, transaction, actorId, "server.shutdown.deferred", "server", _serverId,
                    JsonSerializer.Serialize(new { request.Reason, unresolved }), ct);
                return;
            }
            await SetMetaAsync(connection, transaction, "mode", TaskServerMode.Maintenance.ToString(), ct);
            _mode = TaskServerMode.Maintenance;
            result = new PrepareShutdownResult(true, 0, _mode, "No attempt authority is unresolved. The supervised process may stop.");
            await AuditAsync(connection, transaction, actorId, "server.shutdown.prepared", "server", _serverId,
                JsonSerializer.Serialize(new { request.Reason }), ct);
        }, ct, requireReady: false);
        return result!;
    }

    public async Task<LeaseResponse> ResolveUnknownAttemptAsync(string runId, ResolveUnknownAttemptRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.ContainmentProof))
            throw new ArgumentException("Positive containment proof is required.");
        LeaseDto? resolved = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Run lease was not found.");
            if (!string.Equals(lease.Status, "process-unknown", StringComparison.Ordinal))
                throw new TaskServerConflictException("attempt-not-unknown", $"Lease status is '{lease.Status}'.");
            var targetState = string.Equals(request.Resolution, "requeue", StringComparison.OrdinalIgnoreCase)
                ? "2-ready"
                : "5-human-review";
            await ExecuteAsync(connection, """
                UPDATE leases SET status = 'fenced' WHERE run_id = $run;
                UPDATE runs SET status = 'interrupted', finished_at = $now WHERE id = $run;
                UPDATE tasks SET state = $state, version = version + 1, updated_at = $now WHERE id = $task;
                """, ct, transaction, ("$run", runId), ("$now", Iso(UtcNow)), ("$state", targetState), ("$task", lease.TaskId));
            await AuditAsync(connection, transaction, actorId, "attempt.unknown.resolved", "run", runId,
                JsonSerializer.Serialize(new { request.ContainmentProof, request.Resolution, lease.Fence }), ct);
            resolved = lease with { Status = "fenced" };
        }, ct);
        return new LeaseResponse("fenced", resolved, "The old authority was closed using audited containment proof.");
    }

    public async Task<BackupResult> CreateBackupAsync(BackupRequest request, string actorId, CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Task Server is not ready.");
        await _writeGate.WaitAsync(ct);
        try
        {
            var safeName = SanitizeBackupName(request.Name);
            var backupId = $"{UtcNow:yyyyMMddHHmmssfff}-{safeName}-{Guid.NewGuid():N}";
            var path = Path.Combine(BackupDirectory, backupId + ".db");
            await using var source = Open();
            await source.OpenAsync(ct);
            await ConfigureConnectionAsync(source, ct);
            // Write and verify the snapshot inside a nested scope so the destination
            // connection is fully closed before the file is hashed or the .db is later
            // moved/deleted. Pooling stays off so dispose actually releases the OS file
            // handle instead of parking it in the pool ("used by another process").
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                await destination.OpenAsync(ct);
                source.BackupDatabase(destination);
                var integrity = Convert.ToString(await ScalarAsync(destination, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Backup integrity check failed: {integrity}");
            }
            var sha = await HashFileAsync(path, ct);
            var info = new FileInfo(path);
            await using var transaction = (SqliteTransaction)await source.BeginTransactionAsync(ct);
            await AuditAsync(source, transaction, actorId, "backup.created", "backup", backupId,
                JsonSerializer.Serialize(new { sha256 = sha, info.Length }), ct);
            await transaction.CommitAsync(ct);
            return new BackupResult(backupId, path, sha, UtcNow, info.Length);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RestoreResult> RestoreBackupAsync(RestoreRequest request, string actorId, CancellationToken ct)
    {
        var path = ResolveBackupPath(request.BackupId);
        if (!File.Exists(path)) throw new KeyNotFoundException("Backup was not found.");
        var sha = await HashFileAsync(path, ct);
        await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Pooling off so the backup file handle is released on dispose and the
            // .db can be moved/deleted afterwards.
            Pooling = false,
        }.ToString()))
        {
            await verify.OpenAsync(ct);
            var integrity = Convert.ToString(await ScalarAsync(verify, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return new RestoreResult(request.BackupId, false, false, sha, $"Integrity check failed: {integrity}");
        }

        if (request.VerifyOnly)
            return new RestoreResult(request.BackupId, true, false, sha, "Backup verified; no data was changed.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Restore requires maintenance mode.");

        await _writeGate.WaitAsync(ct);
        try
        {
            var safety = DatabasePath + $".pre-restore-{Guid.NewGuid():N}";
            await using (var current = Open())
            {
                await current.OpenAsync(ct);
                await ConfigureConnectionAsync(current, ct);
                var unresolved = Convert.ToInt64(await ScalarAsync(current,
                    """
                    SELECT
                        (SELECT count(*) FROM leases
                          WHERE status IN ('active', 'process-unknown'))
                      + (SELECT count(*) FROM review_attempts
                          WHERE status IN ('leased', 'process-unknown'));
                    """, ct) ?? 0L, CultureInfo.InvariantCulture);
                if (unresolved > 0)
                    throw new TaskServerConflictException("attempt-authority-unresolved", "Restore is blocked while active or process-unknown attempts exist.");

                await using var safetyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = safety,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
                await safetyConnection.OpenAsync(ct);
                current.BackupDatabase(safetyConnection);
            }

            AuthorityReady = false;
            var staging = DatabasePath + ".restore";
            try
            {
                File.Copy(path, staging, overwrite: true);
                DeleteDatabaseSidecars();
                File.Move(staging, DatabasePath, overwrite: true);

                await using (var restored = Open())
                {
                    await restored.OpenAsync(ct);
                    await ConfigureConnectionAsync(restored, ct);
                    await ApplyMigrationsAsync(restored, ct);
                    var integrity = Convert.ToString(await ScalarAsync(restored, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Restored store integrity check failed: {integrity}");
                    _serverId = await GetOrCreateMetaAsync(restored, "server_id", $"srv_{Guid.NewGuid():N}", ct);
                    await using var transaction = (SqliteTransaction)await restored.BeginTransactionAsync(ct);
                    await SetMetaAsync(restored, transaction, "mode", TaskServerMode.Maintenance.ToString(), ct);
                    _mode = TaskServerMode.Maintenance;
                    await AuditAsync(restored, transaction, actorId, "backup.restored", "backup", request.BackupId,
                        JsonSerializer.Serialize(new { sha256 = sha }), ct);
                    await transaction.CommitAsync(ct);
                }

                AuthorityReady = true;
                File.Delete(safety);
                return new RestoreResult(request.BackupId, true, true, sha, "Backup restored with identity and fence continuity.");
            }
            catch (Exception restoreException)
            {
                try
                {
                    DeleteDatabaseSidecars();
                    File.Move(safety, DatabasePath, overwrite: true);
                    await using var rolledBack = Open();
                    await rolledBack.OpenAsync(ct);
                    await ConfigureConnectionAsync(rolledBack, ct);
                    var integrity = Convert.ToString(await ScalarAsync(rolledBack, "PRAGMA integrity_check;", ct), CultureInfo.InvariantCulture);
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Pre-restore safety copy integrity check failed: {integrity}");
                    _serverId = await GetOrCreateMetaAsync(rolledBack, "server_id", $"srv_{Guid.NewGuid():N}", ct);
                    var storedMode = await GetOrCreateMetaAsync(rolledBack, "mode", TaskServerMode.Maintenance.ToString(), ct);
                    _mode = Enum.TryParse<TaskServerMode>(storedMode, true, out var parsed) ? parsed : TaskServerMode.Maintenance;
                    AuthorityReady = true;
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Restore failed and the pre-restore safety copy could not be recovered. Task Server remains not ready.",
                        restoreException,
                        rollbackException);
                }

                throw;
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditRecordDto>> ListAuditAsync(long after, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT sequence, occurred_at, actor_id, action, target_type, target_id, detail_json
              FROM audit WHERE sequence > $after ORDER BY sequence LIMIT 1000;
            """, ("$after", after));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AuditRecordDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new AuditRecordDto(reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return result;
    }

    internal async Task<string> ComputeIntegrityDigestAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var builder = new StringBuilder();
        foreach (var table in new[]
                 {
                     "workspaces", "projects", "tasks", "runs", "events", "artifacts",
                     "audit", "fence_counters", "leases", "runners", "review_subjects",
                     "review_attempts", "review_fence_counters", "review_deliveries",
                 })
        {
            var count = Convert.ToInt64(await ScalarAsync(connection, $"SELECT count(*) FROM {table};", ct) ?? 0L, CultureInfo.InvariantCulture);
            builder.Append(table).Append(':').Append(count).Append('\n');
        }
        await using var command = Command(connection, "SELECT id, task_key, version FROM tasks ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            builder.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetInt64(2)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    internal async Task ImportLegacyBatchAsync(
        string workspaceName,
        IReadOnlyList<LegacyProjectImport> projects,
        string actorId,
        CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Legacy import requires maintenance mode.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var workspaceId = DeterministicId("wsp", workspaceName);
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO workspaces(id, name, version, created_at, updated_at)
                VALUES ($id, $name, 1, $now, $now)
                ON CONFLICT(id) DO NOTHING;
                """, ct, transaction, ("$id", workspaceId), ("$name", workspaceName), ("$now", now));

            foreach (var project in projects)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO projects(id, workspace_id, name, task_key_prefix, next_task_number, version, created_at, updated_at)
                    VALUES ($id, $workspace, $name, $prefix, $next, 1, $now, $now)
                    ON CONFLICT(id) DO NOTHING;
                    """, ct, transaction,
                    ("$id", project.ProjectId), ("$workspace", workspaceId), ("$name", project.Name),
                    ("$prefix", project.Prefix), ("$next", project.NextTaskNumber), ("$now", now));

                foreach (var task in project.Tasks)
                {
                    await ExecuteAsync(connection, """
                        INSERT INTO tasks(id, project_id, task_key, title, body, state, version, created_at, updated_at)
                        VALUES ($id, $project, $key, $title, $body, $state, 1, $created, $updated)
                        ON CONFLICT(id) DO NOTHING;
                        """, ct, transaction,
                        ("$id", task.TaskId), ("$project", project.ProjectId), ("$key", task.TaskKey),
                        ("$title", task.Title), ("$body", task.Body), ("$state", task.State),
                        ("$created", Iso(task.CreatedAt)), ("$updated", Iso(task.UpdatedAt)));

                    foreach (var legacyEvent in task.Events)
                    {
                        await ExecuteAsync(connection, """
                            INSERT INTO events(event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at)
                            VALUES ($id, '', $task, $kind, $payload, $key, 0, $occurred)
                            ON CONFLICT(idempotency_key) DO NOTHING;
                            """, ct, transaction,
                            ("$id", legacyEvent.EventId), ("$task", task.TaskId), ("$kind", legacyEvent.Kind),
                            ("$payload", legacyEvent.PayloadJson), ("$key", legacyEvent.IdempotencyKey), ("$occurred", Iso(legacyEvent.OccurredAt)));
                    }

                    foreach (var artifact in task.Artifacts)
                    {
                        await ExecuteAsync(connection, """
                            INSERT INTO artifacts(id, run_id, name, media_type, sha256, content, size_bytes, idempotency_key, fence, created_at)
                            VALUES ($id, '', $name, $media, $sha, $content, $size, $key, 0, $created)
                            ON CONFLICT(idempotency_key) DO NOTHING;
                            """, ct, transaction,
                            ("$id", artifact.ArtifactId), ("$name", artifact.Name), ("$media", artifact.MediaType),
                            ("$sha", artifact.Sha256), ("$content", artifact.Content), ("$size", artifact.Content.LongLength),
                            ("$key", artifact.IdempotencyKey), ("$created", Iso(artifact.CreatedAt)));
                    }
                }
            }

            await AuditAsync(connection, transaction, actorId, "legacy.imported", "server", _serverId,
                JsonSerializer.Serialize(new { workspaceName, projects = projects.Count, tasks = projects.Sum(p => p.Tasks.Count) }), ct);
        }, ct);
    }

    private async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);", ct);
        var storedVersion = Convert.ToInt32(
            await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'schema_version';", ct) ?? 0,
            CultureInfo.InvariantCulture);
        if (storedVersion > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Task Server store schema {storedVersion} is newer than this service supports ({CurrentSchemaVersion}).");

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspaces(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS projects(
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL REFERENCES workspaces(id),
                name TEXT NOT NULL,
                task_key_prefix TEXT NOT NULL UNIQUE,
                next_task_number INTEGER NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                task_key TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                body TEXT,
                state TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runners(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                host_id TEXT NOT NULL,
                instance_id TEXT NOT NULL,
                runner_version TEXT NOT NULL,
                protocol_version INTEGER NOT NULL,
                capabilities_json TEXT NOT NULL,
                status TEXT NOT NULL,
                registered_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs(
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                status TEXT NOT NULL,
                runner_id TEXT REFERENCES runners(id),
                fence INTEGER,
                created_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT
            );
            CREATE TABLE IF NOT EXISTS fence_counters(
                task_id TEXT PRIMARY KEY REFERENCES tasks(id),
                last_fence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS leases(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                lease_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL UNIQUE REFERENCES runs(id),
                runner_id TEXT NOT NULL REFERENCES runners(id),
                instance_id TEXT NOT NULL,
                fence INTEGER NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS events(
                cursor INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                run_id TEXT NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                fence INTEGER NOT NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifacts(
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                content BLOB NOT NULL,
                size_bytes INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                fence INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action TEXT NOT NULL,
                target_type TEXT NOT NULL,
                target_id TEXT NOT NULL,
                detail_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_project_state ON tasks(project_id, state);
            CREATE INDEX IF NOT EXISTS ix_leases_task_status ON leases(task_id, status);
            CREATE INDEX IF NOT EXISTS ix_events_run_cursor ON events(run_id, cursor);
            CREATE INDEX IF NOT EXISTS ix_artifacts_run ON artifacts(run_id);
            """, ct);
        await ExecuteAsync(connection, """
            INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $now)
            ON CONFLICT(version) DO NOTHING;
            """, ct, ("$version", CurrentSchemaVersion), ("$now", Iso(UtcNow)));
        await ApplyReviewMigrationAsync(connection, ct);
        await SetMetaAsync(connection, null, "schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture), ct);
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = false,
    }.ToString());

    private async Task<SqliteConnection> OpenReadyAsync(CancellationToken ct)
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        var connection = Open();
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", ct);

    private async Task InWriteTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken ct,
        bool requireReady = true)
    {
        if (requireReady && !AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = Open();
            await connection.OpenAsync(ct);
            await ConfigureConnectionAsync(connection, ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await action(connection, transaction);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RequireWritable()
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance)
            throw new TaskServerConflictException("server-not-writable", $"Task Server is in {_mode} mode.");
    }

    private void RequireAdmission()
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode != TaskServerMode.Normal)
            throw new TaskServerConflictException("admission-closed", $"New claims are closed while Task Server is in {_mode} mode.");
    }

    private int NormalizeTtl(int requested)
        => Math.Clamp(requested, _options.MinimumLeaseSeconds, _options.MaximumLeaseSeconds);

    private static void ValidateLeaseReference(LeaseDto lease, string runnerId, string instanceId, string leaseId, long fence)
    {
        if (!string.Equals(lease.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || lease.Fence != fence)
            throw new TaskServerConflictException("stale-fence", "Lease id, runner instance, or fence does not match current authority.");
    }

    private async Task EnsureLeaseCurrentAsync(SqliteConnection connection, SqliteTransaction transaction, LeaseDto lease, CancellationToken ct)
    {
        if (!string.Equals(lease.Status, "active", StringComparison.Ordinal))
            throw new TaskServerConflictException("lease-not-active", $"Lease status is '{lease.Status}'.");
        if (lease.ExpiresAt <= UtcNow)
            throw new TaskServerConflictException("lease-expired-process-unknown", "Lease expired. Evidence is retained, but authoritative writes are fenced off.");
        var lastFence = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT last_fence FROM fence_counters WHERE task_id = $task;", ct, transaction, ("$task", lease.TaskId)) ?? 0L,
            CultureInfo.InvariantCulture);
        if (lastFence != lease.Fence)
            throw new TaskServerConflictException("stale-fence", "A higher durable fence exists for this task.");
    }

    private static async Task ValidateRunnerAsync(SqliteConnection connection, SqliteTransaction transaction, string runnerId, string instanceId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT instance_id, protocol_version, status, capabilities_json FROM runners WHERE id = $id;", transaction, ("$id", runnerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Runner is not registered.");
        if (!string.Equals(reader.GetString(0), instanceId, StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-instance-stale", "Runner instance id is not current.");
        var protocol = reader.GetInt32(1);
        if (!TaskServerProtocol.Supports(protocol)) throw new TaskServerProtocolException(protocol);
        if (!string.Equals(reader.GetString(2), "active", StringComparison.Ordinal))
            throw new TaskServerConflictException("runner-not-active", "Runner is not active.");
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        if (capabilities.Contains(ReviewCapabilities.ReviewExecutor, StringComparer.Ordinal))
            throw new TaskServerConflictException(
                "coding-capability-required",
                "A separately registered Remote Review Executor cannot claim coding work.");
    }

    private static async Task<(string Prefix, long Next)> ReadProjectCounterAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT task_key_prefix, next_task_number FROM projects WHERE id = $id;", transaction, ("$id", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Project was not found.");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<TaskDto?> ReadTaskAsync(
        SqliteConnection connection, SqliteTransaction transaction, string projectId, string identity, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, task_key, title, state, version, created_at, updated_at, body
              FROM tasks WHERE project_id = $project AND (id = $identity OR task_key = upper($identity));
            """, transaction, ("$project", projectId), ("$identity", identity));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    private static TaskDto ReadTask(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt64(5), Parse(reader.GetString(6)), Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static async Task<LeaseDto?> ReadLeaseAsync(SqliteConnection connection, SqliteTransaction transaction, string runId, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT lease_id, run_id, task_id, runner_id, instance_id, fence, acquired_at, expires_at, status
              FROM leases WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LeaseDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), Parse(reader.GetString(6)), Parse(reader.GetString(7)), reader.GetString(8))
            : null;
    }

    private static async Task<EventDto?> ReadEventByIdempotencyKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT cursor, event_id, run_id, task_id, kind, payload_json, idempotency_key, fence, occurred_at
              FROM events WHERE idempotency_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEvent(reader) : null;
    }

    private static EventDto ReadEvent(SqliteDataReader reader)
        => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetInt64(7), Parse(reader.GetString(8)));

    private static void ValidateEventReplay(EventDto? existing, string runId, string taskId, EventIngestRequest request)
    {
        if (existing is null)
            throw new InvalidOperationException("The ingested event could not be read back.");
        if (!string.Equals(existing.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(existing.TaskId, taskId, StringComparison.Ordinal)
            || !string.Equals(existing.Kind, request.Kind, StringComparison.Ordinal)
            || !string.Equals(existing.PayloadJson, request.PayloadJson, StringComparison.Ordinal)
            || existing.Fence != request.Fence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The event idempotency key is already bound to a different run or payload.");
        }
    }

    private static async Task<ArtifactDto?> ReadArtifactByIdempotencyKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, run_id, name, media_type, sha256, size_bytes, idempotency_key, fence, created_at
              FROM artifacts WHERE idempotency_key = $key;
            """, transaction, ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadArtifact(reader) : null;
    }

    private static ArtifactDto ReadArtifact(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt64(5), reader.GetString(6), reader.GetInt64(7), Parse(reader.GetString(8)));

    private static void ValidateArtifactReplay(
        ArtifactDto? existing,
        string runId,
        ArtifactIngestRequest request,
        string actualSha,
        long sizeBytes)
    {
        if (existing is null)
            throw new InvalidOperationException("The ingested artifact could not be read back.");
        if (!string.Equals(existing.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(existing.Name, request.Name, StringComparison.Ordinal)
            || !string.Equals(existing.MediaType, request.MediaType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Sha256, actualSha, StringComparison.OrdinalIgnoreCase)
            || existing.SizeBytes != sizeBytes
            || existing.Fence != request.Fence)
        {
            throw new TaskServerConflictException(
                "idempotency-conflict",
                "The artifact idempotency key is already bound to a different run or payload.");
        }
    }

    private async Task<string> GetOrCreateMetaAsync(SqliteConnection connection, string key, string defaultValue, CancellationToken ct)
    {
        var existing = Convert.ToString(await ScalarAsync(connection, "SELECT value FROM meta WHERE key = $key;", ct, ("$key", key)), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        await ExecuteAsync(connection, "INSERT INTO meta(key, value) VALUES ($key, $value);", ct, ("$key", key), ("$value", defaultValue));
        return defaultValue;
    }

    private static async Task SetMetaAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string value, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO meta(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, ct, transaction, ("$key", key), ("$value", value));

    private async Task AuditAsync(
        SqliteConnection connection, SqliteTransaction transaction, string actorId, string action,
        string targetType, string targetId, string detailJson, CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO audit(occurred_at, actor_id, action, target_type, target_id, detail_json)
            VALUES ($at, $actor, $action, $type, $target, $detail);
            """, ct, transaction,
            ("$at", Iso(UtcNow)), ("$actor", string.IsNullOrWhiteSpace(actorId) ? "anonymous-local" : actorId),
            ("$action", action), ("$type", targetType), ("$target", targetId), ("$detail", detailJson));

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
        => Command(connection, sql, null, parameters);

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            if (!string.IsNullOrWhiteSpace(name)) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        => await ExecuteAsync(connection, sql, ct, null, parameters);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        => await ScalarAsync(connection, sql, ct, null, parameters);

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    private string ResolveBackupPath(string backupId)
    {
        if (!string.Equals(Path.GetFileName(backupId), backupId, StringComparison.Ordinal)
            || backupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Backup id is invalid.");
        return Path.Combine(BackupDirectory, backupId.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? backupId : backupId + ".db");
    }

    private void DeleteDatabaseSidecars()
    {
        if (File.Exists(DatabasePath + "-wal")) File.Delete(DatabasePath + "-wal");
        if (File.Exists(DatabasePath + "-shm")) File.Delete(DatabasePath + "-shm");
    }

    private static string SanitizeBackupName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "manual" : name.Trim().ToLowerInvariant();
        var clean = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? "manual" : clean[..Math.Min(clean.Length, 40)];
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static string DeterministicId(string prefix, string identity)
        => $"{prefix}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24]}";

    private static string StableOrGeneratedId(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{prefix}_{Guid.NewGuid():N}";
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Resource ids may contain only letters, digits, '.', '_', and '-'.");
        return normalized;
    }

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

public sealed class TaskServerConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class TaskServerProtocolException(int version)
    : Exception($"Protocol {version} is outside the supported range {TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}.")
{
    public int Version { get; } = version;
}

internal sealed record LegacyProjectImport(
    string ProjectId,
    string Name,
    string Prefix,
    long NextTaskNumber,
    IReadOnlyList<LegacyTaskImport> Tasks);

internal sealed record LegacyTaskImport(
    string TaskId,
    string TaskKey,
    string Title,
    string? Body,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<LegacyEventImport> Events,
    IReadOnlyList<LegacyArtifactImport> Artifacts);

internal sealed record LegacyEventImport(
    string EventId,
    string Kind,
    string PayloadJson,
    string IdempotencyKey,
    DateTime OccurredAt);

internal sealed record LegacyArtifactImport(
    string ArtifactId,
    string Name,
    string MediaType,
    byte[] Content,
    string Sha256,
    string IdempotencyKey,
    DateTime CreatedAt);
