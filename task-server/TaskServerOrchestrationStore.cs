using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<FlowDefinitionDto> UpsertFlowDefinitionAsync(
        string projectId,
        UpsertFlowDefinitionRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateFlowDefinition(request);
        FlowDefinitionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var currentVersion = Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT version FROM flow_definitions WHERE project_id = $project;",
                ct,
                transaction,
                ("$project", projectId)) ?? 0L,
                CultureInfo.InvariantCulture);
            if (currentVersion == 0)
            {
                _ = await ScalarAsync(
                    connection,
                    "SELECT id FROM projects WHERE id = $project;",
                    ct,
                    transaction,
                    ("$project", projectId))
                    ?? throw new KeyNotFoundException("Project was not found.");
                if (request.ExpectedVersion is not null and not 0)
                    throw new TaskServerConflictException(
                        "resource-version-mismatch",
                        "The flow definition does not exist.");
            }
            else if (request.ExpectedVersion != currentVersion)
            {
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected flow definition version {request.ExpectedVersion?.ToString() ?? "missing"}, current version is {currentVersion}.");
            }

            var nextVersion = currentVersion + 1;
            var updatedAt = UtcNow;
            var stagesJson = JsonSerializer.Serialize(request.Stages);
            await ExecuteAsync(connection, """
                INSERT INTO flow_definitions(project_id, version, stages_json, max_reissue_attempts, updated_at)
                VALUES ($project, $version, $stages, $max_reissues, $updated)
                ON CONFLICT(project_id) DO UPDATE SET
                    version = excluded.version,
                    stages_json = excluded.stages_json,
                    max_reissue_attempts = excluded.max_reissue_attempts,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", projectId),
                ("$version", nextVersion),
                ("$stages", stagesJson),
                ("$max_reissues", request.MaxReissueAttempts),
                ("$updated", Iso(updatedAt)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.flow-definition-upserted",
                "project",
                projectId,
                JsonSerializer.Serialize(new { version = nextVersion, request.Stages, request.MaxReissueAttempts }),
                ct);
            result = new FlowDefinitionDto(
                projectId,
                nextVersion,
                request.Stages.ToArray(),
                request.MaxReissueAttempts,
                updatedAt);
        }, ct);
        return result!;
    }

    public async Task<FlowDefinitionDto?> GetFlowDefinitionAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT project_id, version, stages_json, max_reissue_attempts, updated_at
              FROM flow_definitions
             WHERE project_id = $project;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFlowDefinition(reader) : null;
    }

    public async Task<OrchestrationRunDto> CreateOrchestrationRunAsync(
        string projectId,
        CreateOrchestrationRunRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.TaskId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Task id and idempotency key are required.");
        ValidateJson(request.PayloadJson, "payloadJson");

        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadOrchestrationRunByKeyAsync(
                connection,
                transaction,
                request.IdempotencyKey,
                ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.ProjectId, projectId, StringComparison.Ordinal)
                    || !string.Equals(existing.TaskId, request.TaskId, StringComparison.Ordinal)
                    || !JsonEquivalent(existing.PayloadJson, request.PayloadJson))
                    throw new TaskServerConflictException(
                        "idempotency-conflict",
                        "The orchestration idempotency key is already bound to different input.");
                result = existing;
                return;
            }

            var taskProject = Convert.ToString(await ScalarAsync(
                connection,
                "SELECT project_id FROM tasks WHERE id = $task;",
                ct,
                transaction,
                ("$task", request.TaskId)),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(taskProject)
                || !string.Equals(taskProject, projectId, StringComparison.Ordinal))
                throw new KeyNotFoundException("Task was not found in the requested project.");

            var definition = await ReadFlowDefinitionAsync(connection, transaction, projectId, ct)
                ?? throw new TaskServerConflictException(
                    "flow-definition-missing",
                    "The project has no orchestration flow definition.");
            var now = UtcNow;
            var runId = $"orch_{Guid.NewGuid():N}";
            await ExecuteAsync(connection, """
                INSERT INTO orchestration_runs(
                    id, project_id, task_id, definition_version, stages_json,
                    max_reissue_attempts, status, current_stage, payload_json,
                    idempotency_key, reissue_attempts, created_at, updated_at)
                VALUES (
                    $id, $project, $task, $definition_version, $stages,
                    $max_reissues, 'pending', $stage, $payload,
                    $key, 0, $now, $now);
                """, ct, transaction,
                ("$id", runId),
                ("$project", projectId),
                ("$task", request.TaskId),
                ("$definition_version", definition.Version),
                ("$stages", JsonSerializer.Serialize(definition.Stages)),
                ("$max_reissues", definition.MaxReissueAttempts),
                ("$stage", definition.Stages[0].ToString()),
                ("$payload", request.PayloadJson),
                ("$key", request.IdempotencyKey),
                ("$now", Iso(now)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.run-created",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { projectId, request.TaskId, definition.Version }),
                ct);
            result = new OrchestrationRunDto(
                runId,
                projectId,
                request.TaskId,
                definition.Version,
                "pending",
                definition.Stages[0],
                request.PayloadJson,
                0,
                now,
                now,
                null,
                []);
        }, ct);
        return result!;
    }

    public async Task<OrchestrationRunDto?> GetOrchestrationRunAsync(
        string runId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadOrchestrationRunAsync(connection, null, runId, ct);
    }

    public async Task<IReadOnlyList<OrchestrationRunDto>> ListOrchestrationRunsAsync(
        string? projectId,
        string? status,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var sql = """
            SELECT id FROM orchestration_runs
             WHERE ($project IS NULL OR project_id = $project)
               AND ($status IS NULL OR status = $status)
             ORDER BY created_at, id;
            """;
        await using var command = Command(
            connection,
            sql,
            ("$project", string.IsNullOrWhiteSpace(projectId) ? null : projectId),
            ("$status", string.IsNullOrWhiteSpace(status) ? null : status));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        await reader.DisposeAsync();

        var runs = new List<OrchestrationRunDto>(ids.Count);
        foreach (var id in ids)
        {
            var run = await ReadOrchestrationRunAsync(connection, null, id, ct);
            if (run is not null) runs.Add(run);
        }
        return runs;
    }

    public async Task<OrchestrationClaimResponse> ClaimOrchestrationAsync(
        OrchestrationClaimRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireAdmission();
        ValidateEngineIdentity(request.EngineId, request.InstanceId);
        if (request.SupportedStages.Count == 0)
            throw new ArgumentException("At least one supported orchestration stage is required.");
        OrchestrationClaimResponse? response = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = UtcNow;
            await ExpireOrchestrationLeasesAsync(connection, transaction, now, ct);
            var supported = request.SupportedStages.ToHashSet();
            string? runId = null;
            await using (var command = Command(connection, """
                SELECT id, current_stage
                  FROM orchestration_runs
                 WHERE status = 'pending'
                 ORDER BY updated_at, created_at, id;
                """, transaction))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (Enum.TryParse<OrchestrationStage>(reader.GetString(1), out var stage)
                        && supported.Contains(stage))
                    {
                        runId = reader.GetString(0);
                        break;
                    }
                }
            }

            if (runId is null)
            {
                response = new OrchestrationClaimResponse("empty");
                return;
            }

            var lastFence = Convert.ToInt64(await ScalarAsync(
                connection,
                "SELECT last_fence FROM orchestration_fence_counters WHERE run_id = $run;",
                ct,
                transaction,
                ("$run", runId)) ?? 0L,
                CultureInfo.InvariantCulture);
            var fence = checked(lastFence + 1);
            var ttl = NormalizeTtl(request.RequestedTtlSeconds);
            var leaseId = $"olease_{Guid.NewGuid():N}";
            var expires = now.AddSeconds(ttl);
            await ExecuteAsync(connection, """
                INSERT INTO orchestration_fence_counters(run_id, last_fence)
                VALUES ($run, $fence)
                ON CONFLICT(run_id) DO UPDATE SET last_fence = excluded.last_fence;
                INSERT INTO orchestration_leases(
                    run_id, lease_id, engine_id, instance_id, fence,
                    acquired_at, expires_at, status)
                VALUES ($run, $lease, $engine, $instance, $fence, $now, $expires, 'active')
                ON CONFLICT(run_id) DO UPDATE SET
                    lease_id = excluded.lease_id,
                    engine_id = excluded.engine_id,
                    instance_id = excluded.instance_id,
                    fence = excluded.fence,
                    acquired_at = excluded.acquired_at,
                    expires_at = excluded.expires_at,
                    status = excluded.status;
                UPDATE orchestration_runs
                   SET status = 'leased', updated_at = $now
                 WHERE id = $run AND status = 'pending';
                """, ct, transaction,
                ("$run", runId),
                ("$fence", fence),
                ("$lease", leaseId),
                ("$engine", request.EngineId),
                ("$instance", request.InstanceId),
                ("$now", Iso(now)),
                ("$expires", Iso(expires)));
            var run = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Claimed orchestration run disappeared.");
            var lease = new OrchestrationLeaseDto(
                leaseId,
                runId,
                request.EngineId,
                request.InstanceId,
                fence,
                now,
                expires,
                "active");
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.claimed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.InstanceId, fence, run.CurrentStage }),
                ct);
            response = new OrchestrationClaimResponse("claimed", run, lease);
        }, ct);
        return response!;
    }

    public async Task<OrchestrationLeaseDto> RenewOrchestrationLeaseAsync(
        string runId,
        OrchestrationLeaseRenewRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestrationLeaseDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var expires = UtcNow.AddSeconds(NormalizeTtl(request.RequestedTtlSeconds));
            await ExecuteAsync(connection, """
                UPDATE orchestration_leases SET expires_at = $expires WHERE run_id = $run;
                """, ct, transaction, ("$expires", Iso(expires)), ("$run", runId));
            result = lease with { ExpiresAt = expires };
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.lease-renewed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.Fence }),
                ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestrationRunDto> CompleteOrchestrationStageAsync(
        string runId,
        CompleteOrchestrationStageRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.");
        ValidateJson(request.OutputJson, "outputJson");
        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var replay = await ReadStageResultByKeyAsync(connection, transaction, request.IdempotencyKey, ct);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.RunId, runId, StringComparison.Ordinal)
                    || replay.Value.Result.Stage != request.Stage
                    || replay.Value.Result.Action != request.Action
                    || !JsonEquivalent(replay.Value.Result.OutputJson, request.OutputJson))
                    throw new TaskServerConflictException(
                        "idempotency-conflict",
                        "The stage result idempotency key is already bound to different output.");
                result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                    ?? throw new KeyNotFoundException("Orchestration run was not found.");
                return;
            }

            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var run = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration run was not found.");
            if (run.Status != "leased" || run.CurrentStage != request.Stage)
                throw new TaskServerConflictException(
                    "orchestration-stage-mismatch",
                    $"Run is '{run.Status}' at {run.CurrentStage}, not leased at {request.Stage}.");

            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO orchestration_stage_results(
                    run_id, stage, action, output_json, idempotency_key, completed_at)
                VALUES ($run, $stage, $action, $output, $key, $completed);
                UPDATE orchestration_leases SET status = 'settled' WHERE run_id = $run;
                """, ct, transaction,
                ("$run", runId),
                ("$stage", request.Stage.ToString()),
                ("$action", request.Action.ToString()),
                ("$output", request.OutputJson),
                ("$key", request.IdempotencyKey),
                ("$completed", Iso(now)));

            var stages = await ReadRunStagesAsync(connection, transaction, runId, ct);
            var nextStatus = "pending";
            var nextStage = run.CurrentStage;
            DateTime? completedAt = null;
            var reissues = run.ReissueAttempts;
            string? taskState = null;

            switch (request.Action)
            {
                case OrchestrationAction.Continue:
                    var index = stages.IndexOf(run.CurrentStage);
                    if (index >= 0 && index + 1 < stages.Count)
                        nextStage = stages[index + 1];
                    else
                    {
                        nextStatus = "completed";
                        completedAt = now;
                        taskState = "5-human-review";
                    }
                    break;
                case OrchestrationAction.Reissue:
                    var maxReissues = await ReadRunMaxReissuesAsync(connection, transaction, runId, ct);
                    reissues++;
                    if (reissues <= maxReissues)
                        nextStage = stages[0];
                    else
                    {
                        nextStatus = "escalated";
                        completedAt = now;
                        taskState = "5e-escalated";
                    }
                    break;
                case OrchestrationAction.Escalate:
                    nextStatus = "escalated";
                    completedAt = now;
                    taskState = "5e-escalated";
                    break;
                case OrchestrationAction.Complete:
                    nextStatus = "completed";
                    completedAt = now;
                    taskState = "5-human-review";
                    break;
                case OrchestrationAction.Fail:
                    nextStatus = "failed";
                    completedAt = now;
                    taskState = "5e-escalated";
                    break;
            }

            await ExecuteAsync(connection, """
                UPDATE orchestration_runs
                   SET status = $status,
                       current_stage = $stage,
                       reissue_attempts = $reissues,
                       updated_at = $updated,
                       completed_at = $completed
                 WHERE id = $run;
                """, ct, transaction,
                ("$status", nextStatus),
                ("$stage", nextStage.ToString()),
                ("$reissues", reissues),
                ("$updated", Iso(now)),
                ("$completed", completedAt is null ? null : Iso(completedAt.Value)),
                ("$run", runId));
            if (taskState is not null)
            {
                await ExecuteAsync(connection, """
                    UPDATE tasks
                       SET state = $state, version = version + 1, updated_at = $updated
                     WHERE id = $task;
                    """, ct, transaction,
                    ("$state", taskState),
                    ("$updated", Iso(now)),
                    ("$task", run.TaskId));
            }
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.stage-completed",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.Stage, request.Action, request.Fence, nextStatus, nextStage }),
                ct);
            result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Settled orchestration run disappeared.");
        }, ct);
        return result!;
    }

    public async Task<OrchestrationRunDto> ReleaseOrchestrationLeaseAsync(
        string runId,
        ReleaseOrchestrationLeaseRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestrationRunDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var lease = await ReadOrchestrationLeaseAsync(connection, transaction, runId, ct)
                ?? throw new KeyNotFoundException("Orchestration lease was not found.");
            ValidateOrchestrationLease(lease, request.EngineId, request.InstanceId, request.LeaseId, request.Fence);
            EnsureOrchestrationLeaseActive(lease);
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE orchestration_leases SET status = 'released' WHERE run_id = $run;
                UPDATE orchestration_runs SET status = 'pending', updated_at = $updated WHERE id = $run;
                """, ct, transaction, ("$run", runId), ("$updated", Iso(now)));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "orchestration.lease-released",
                "orchestration-run",
                runId,
                JsonSerializer.Serialize(new { request.EngineId, request.Fence, request.Reason }),
                ct);
            result = await ReadOrchestrationRunAsync(connection, transaction, runId, ct)
                ?? throw new InvalidOperationException("Released orchestration run disappeared.");
        }, ct);
        return result!;
    }

    private static void ValidateFlowDefinition(UpsertFlowDefinitionRequest request)
    {
        if (request.Stages.Count == 0)
            throw new ArgumentException("A flow definition requires at least one stage.");
        if (request.Stages.Distinct().Count() != request.Stages.Count)
            throw new ArgumentException("A flow definition cannot contain duplicate stages.");
        if (request.MaxReissueAttempts is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(request), "Max reissue attempts must be between 0 and 20.");
    }

    private static void ValidateEngineIdentity(string engineId, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(engineId) || string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Engine and instance ids are required.");
    }

    private void EnsureOrchestrationLeaseActive(OrchestrationLeaseDto lease)
    {
        if (lease.Status != "active")
            throw new TaskServerConflictException("lease-not-active", $"Orchestration lease status is '{lease.Status}'.");
        if (lease.ExpiresAt <= UtcNow)
            throw new TaskServerConflictException("lease-expired", "Orchestration lease expired and may be reclaimed.");
    }

    private static void ValidateOrchestrationLease(
        OrchestrationLeaseDto lease,
        string engineId,
        string instanceId,
        string leaseId,
        long fence)
    {
        if (!string.Equals(lease.EngineId, engineId, StringComparison.Ordinal)
            || !string.Equals(lease.InstanceId, instanceId, StringComparison.Ordinal)
            || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || lease.Fence != fence)
            throw new TaskServerConflictException(
                "stale-fence",
                "Orchestration lease id, engine instance, or fence does not match current authority.");
    }

    private static void ValidateJson(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.");
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} must contain valid JSON.", exception);
        }
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftJson = JsonDocument.Parse(left);
        using var rightJson = JsonDocument.Parse(right);
        return JsonSerializer.Serialize(leftJson.RootElement)
            == JsonSerializer.Serialize(rightJson.RootElement);
    }

    private static FlowDefinitionDto ReadFlowDefinition(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetInt64(1),
            JsonSerializer.Deserialize<List<OrchestrationStage>>(reader.GetString(2)) ?? [],
            reader.GetInt32(3),
            Parse(reader.GetString(4)));

    private static async Task<FlowDefinitionDto?> ReadFlowDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT project_id, version, stages_json, max_reissue_attempts, updated_at
              FROM flow_definitions WHERE project_id = $project;
            """, transaction, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFlowDefinition(reader) : null;
    }

    private static async Task<OrchestrationRunDto?> ReadOrchestrationRunByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken ct)
    {
        var runId = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT id FROM orchestration_runs WHERE idempotency_key = $key;",
            ct,
            transaction,
            ("$key", idempotencyKey)),
            CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(runId)
            ? null
            : await ReadOrchestrationRunAsync(connection, transaction, runId, ct);
    }

    private static async Task<OrchestrationRunDto?> ReadOrchestrationRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, project_id, task_id, definition_version, status, current_stage,
                   payload_json, reissue_attempts, created_at, updated_at, completed_at
              FROM orchestration_runs WHERE id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var run = new OrchestrationRunDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            Enum.Parse<OrchestrationStage>(reader.GetString(5)),
            reader.GetString(6),
            reader.GetInt32(7),
            Parse(reader.GetString(8)),
            Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)));
        await reader.DisposeAsync();

        await using var resultsCommand = Command(connection, """
            SELECT sequence, stage, action, output_json, completed_at
              FROM orchestration_stage_results
             WHERE run_id = $run
             ORDER BY sequence;
            """, transaction, ("$run", runId));
        await using var resultsReader = await resultsCommand.ExecuteReaderAsync(ct);
        var results = new List<OrchestrationStageResultDto>();
        while (await resultsReader.ReadAsync(ct))
        {
            results.Add(new OrchestrationStageResultDto(
                resultsReader.GetInt64(0),
                Enum.Parse<OrchestrationStage>(resultsReader.GetString(1)),
                Enum.Parse<OrchestrationAction>(resultsReader.GetString(2)),
                resultsReader.GetString(3),
                Parse(resultsReader.GetString(4))));
        }
        return run with { StageResults = results };
    }

    private static async Task<OrchestrationLeaseDto?> ReadOrchestrationLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT lease_id, run_id, engine_id, instance_id, fence,
                   acquired_at, expires_at, status
              FROM orchestration_leases WHERE run_id = $run;
            """, transaction, ("$run", runId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new OrchestrationLeaseDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                Parse(reader.GetString(5)),
                Parse(reader.GetString(6)),
                reader.GetString(7))
            : null;
    }

    private async Task ExpireOrchestrationLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE orchestration_runs
               SET status = 'pending', updated_at = $now
             WHERE status = 'leased'
               AND id IN (
                   SELECT run_id FROM orchestration_leases
                    WHERE status = 'active' AND expires_at <= $now
               );
            UPDATE orchestration_leases
               SET status = 'expired'
             WHERE status = 'active' AND expires_at <= $now;
            """, ct, transaction, ("$now", Iso(now)));

    private static async Task<List<OrchestrationStage>> ReadRunStagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT stages_json FROM orchestration_runs WHERE id = $run;",
            ct,
            transaction,
            ("$run", runId)),
            CultureInfo.InvariantCulture);
        return JsonSerializer.Deserialize<List<OrchestrationStage>>(json ?? "[]") ?? [];
    }

    private static async Task<int> ReadRunMaxReissuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
        => Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT max_reissue_attempts FROM orchestration_runs WHERE id = $run;",
            ct,
            transaction,
            ("$run", runId)) ?? 0,
            CultureInfo.InvariantCulture);

    private static async Task<(string RunId, OrchestrationStageResultDto Result)?> ReadStageResultByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT run_id, sequence, stage, action, output_json, completed_at
              FROM orchestration_stage_results
             WHERE idempotency_key = $key;
            """, transaction, ("$key", idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (
            reader.GetString(0),
            new OrchestrationStageResultDto(
                reader.GetInt64(1),
                Enum.Parse<OrchestrationStage>(reader.GetString(2)),
                Enum.Parse<OrchestrationAction>(reader.GetString(3)),
                reader.GetString(4),
                Parse(reader.GetString(5))));
    }
}
