using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private const int ContextSummaryMaxLength = 180;

    public async Task<IReadOnlyList<OrchestratorContextDto>> ListOrchestratorContextsAsync(
        bool includeHidden,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = new List<OrchestratorContextDto>();
        await using var command = Command(connection, """
            SELECT c.context_key, c.kind, c.project_id, p.name, c.task_id, t.task_key,
                   c.summary, c.created_at, c.updated_at, c.hidden_at,
                   (SELECT count(*) FROM orchestrator_context_turns turn WHERE turn.context_key = c.context_key),
                   (SELECT turn.model FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key AND turn.model IS NOT NULL
                     ORDER BY turn.sequence DESC LIMIT 1),
                   COALESCE((SELECT sum(turn.input_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.output_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.cache_read_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   COALESCE((SELECT sum(turn.cache_creation_tokens) FROM orchestrator_context_turns turn
                     WHERE turn.context_key = c.context_key), 0),
                   c.title, c.dossier_id, c.dossier_key
              FROM orchestrator_contexts c
              JOIN projects p ON p.id = c.project_id
              LEFT JOIN tasks t ON t.id = c.task_id
             WHERE $include_hidden = 1 OR c.hidden_at IS NULL
             ORDER BY CASE c.kind WHEN 'project' THEN 0 WHEN 'task' THEN 1 ELSE 2 END,
                      COALESCE(NULLIF(c.updated_at, ''), c.created_at) DESC,
                      c.context_key;
            """, ("$include_hidden", includeHidden ? 1 : 0));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadOrchestratorContext(reader));
        return result;
    }

    public async Task<OrchestratorContextDto> EnsureOrchestratorContextAsync(
        string projectIdentity,
        string? taskIdentity,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestratorContextDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveOrchestratorContextTargetAsync(
                connection, transaction, projectIdentity, taskIdentity, ct);
            result = await EnsureOrchestratorContextAsync(
                connection, transaction, target, actorId, ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestratorContextDto> EnsureDossierOrchestratorContextAsync(
        string projectIdentity,
        string dossierIdentity,
        EnsureDossierOrchestratorContextRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        OrchestratorContextDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = await ResolveDossierOrchestratorContextTargetAsync(
                connection, transaction, projectIdentity, dossierIdentity, request, ct);
            result = await EnsureOrchestratorContextAsync(
                connection, transaction, target, actorId, ct);
        }, ct);
        return result!;
    }

    public async Task<OrchestratorContextTranscriptResponse> ReadOrchestratorContextAsync(
        string projectIdentity,
        string? taskIdentity,
        int limit,
        string actorId,
        CancellationToken ct)
    {
        var context = await EnsureOrchestratorContextAsync(
            projectIdentity, taskIdentity, actorId, ct);
        return await ReadOrchestratorContextAsync(context, limit, ct);
    }

    public async Task<OrchestratorContextTranscriptResponse> ReadDossierOrchestratorContextAsync(
        string projectIdentity,
        string dossierIdentity,
        EnsureDossierOrchestratorContextRequest request,
        int limit,
        string actorId,
        CancellationToken ct)
    {
        var context = await EnsureDossierOrchestratorContextAsync(
            projectIdentity, dossierIdentity, request, actorId, ct);
        return await ReadOrchestratorContextAsync(context, limit, ct);
    }

    private async Task<OrchestratorContextTranscriptResponse> ReadOrchestratorContextAsync(
        OrchestratorContextDto context,
        int limit,
        CancellationToken ct)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenReadyAsync(ct);
        var turns = new List<OrchestratorContextTurnDto>();
        await using var command = Command(connection, """
            SELECT turn_id, created_at, role, body, model,
                   input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                   error_message, error_detail, attachments_json, receipt_json
              FROM (
                    SELECT sequence, turn_id, created_at, role, body, model,
                           input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                           error_message, error_detail, attachments_json, receipt_json
                      FROM orchestrator_context_turns
                     WHERE context_key = $context
                     ORDER BY sequence DESC
                     LIMIT $limit
                   ) recent
             ORDER BY sequence;
            """, ("$context", context.ContextKey), ("$limit", boundedLimit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) turns.Add(ReadOrchestratorContextTurn(reader));
        return new OrchestratorContextTranscriptResponse(context, turns);
    }

    public async Task<OrchestratorContextTurnDto> AppendOrchestratorContextTurnAsync(
        string projectIdentity,
        string? taskIdentity,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
        => await AppendOrchestratorContextTurnAsync(
            projectIdentity, taskIdentity, null, null, request, actorId, ct);

    public async Task<OrchestratorContextTurnDto> AppendDossierOrchestratorContextTurnAsync(
        string projectIdentity,
        string dossierIdentity,
        EnsureDossierOrchestratorContextRequest dossier,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
        => await AppendOrchestratorContextTurnAsync(
            projectIdentity, null, dossierIdentity, dossier, request, actorId, ct);

    private async Task<OrchestratorContextTurnDto> AppendOrchestratorContextTurnAsync(
        string projectIdentity,
        string? taskIdentity,
        string? dossierIdentity,
        EnsureDossierOrchestratorContextRequest? dossier,
        AppendOrchestratorContextTurnRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ValidateOrchestratorContextTurn(request.Turn);
        OrchestratorContextTurnDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var target = dossier is null
                ? await ResolveOrchestratorContextTargetAsync(
                    connection, transaction, projectIdentity, taskIdentity, ct)
                : await ResolveDossierOrchestratorContextTargetAsync(
                    connection, transaction, projectIdentity, dossierIdentity!, dossier, ct);
            var context = await EnsureOrchestratorContextAsync(
                connection, transaction, target, actorId, ct);
            var canonicalTurn = request.Turn with
            {
                Receipt = request.Turn.Receipt is null
                    ? null
                    : request.Turn.Receipt with { ContextKey = context.ContextKey },
                Attachments = request.Turn.Attachments?.Select(item =>
                    new OrchestratorContextAttachmentDto(item.Alt, item.RelativePath)).ToArray(),
            };
            var payloadSha = TurnPayloadSha(canonicalTurn);
            var existingSha = Convert.ToString(await ScalarAsync(
                connection,
                "SELECT payload_sha256 FROM orchestrator_context_turns WHERE turn_id = $turn;",
                ct,
                transaction,
                ("$turn", canonicalTurn.TurnId)), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(existingSha))
            {
                if (!string.Equals(existingSha, payloadSha, StringComparison.Ordinal))
                    throw new TaskServerConflictException(
                        "orchestrator-turn-conflict",
                        $"Turn '{canonicalTurn.TurnId}' already exists with different content.");
                result = canonicalTurn;
                return;
            }

            if (canonicalTurn.Receipt is not null)
            {
                var userTurnContext = Convert.ToString(await ScalarAsync(
                    connection,
                    "SELECT context_key FROM orchestrator_context_turns WHERE turn_id = $turn;",
                    ct,
                    transaction,
                    ("$turn", canonicalTurn.Receipt.UserTurnId)), CultureInfo.InvariantCulture);
                if (!string.Equals(userTurnContext, context.ContextKey, StringComparison.Ordinal))
                    throw new TaskServerConflictException(
                        "orchestrator-receipt-user-turn-missing",
                        "A context receipt must reference a persisted user turn in the same context.");
            }

            var usage = canonicalTurn.TokenUsage;
            var receiptJson = canonicalTurn.Receipt is null
                ? null
                : JsonSerializer.Serialize(canonicalTurn.Receipt);
            await ExecuteAsync(connection, """
                INSERT INTO orchestrator_context_turns(
                    context_key, turn_id, created_at, role, body, model,
                    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                    error_message, error_detail, attachments_json, receipt_json, payload_sha256)
                VALUES (
                    $context, $turn, $created, $role, $body, $model,
                    $input, $output, $cache_read, $cache_creation,
                    $error, $detail, $attachments, $receipt, $sha);
                """, ct, transaction,
                ("$context", context.ContextKey),
                ("$turn", canonicalTurn.TurnId),
                ("$created", Iso(canonicalTurn.CreatedAt)),
                ("$role", canonicalTurn.Role),
                ("$body", canonicalTurn.Body),
                ("$model", canonicalTurn.Model),
                ("$input", usage?.InputTokens ?? 0),
                ("$output", usage?.OutputTokens ?? 0),
                ("$cache_read", usage?.CacheReadTokens ?? 0),
                ("$cache_creation", usage?.CacheCreationTokens ?? 0),
                ("$error", canonicalTurn.ErrorMessage),
                ("$detail", canonicalTurn.ErrorDetail),
                ("$attachments", canonicalTurn.Attachments is null
                    ? null
                    : JsonSerializer.Serialize(canonicalTurn.Attachments)),
                ("$receipt", receiptJson),
                ("$sha", payloadSha));

            var summary = string.Equals(canonicalTurn.Role, "user", StringComparison.Ordinal)
                ? BuildContextSummary(canonicalTurn.Body, context.Summary)
                : context.Summary;
            await ExecuteAsync(connection, """
                UPDATE orchestrator_contexts
                   SET summary = $summary, updated_at = $updated
                 WHERE context_key = $context;
                """, ct, transaction,
                ("$summary", summary),
                ("$updated", Iso(canonicalTurn.CreatedAt)),
                ("$context", context.ContextKey));
            await AuditAsync(connection, transaction, actorId, "orchestrator-context.turn-appended",
                "orchestrator-context", context.ContextKey,
                JsonSerializer.Serialize(new
                {
                    canonicalTurn.TurnId,
                    canonicalTurn.Role,
                    receiptId = canonicalTurn.Receipt?.ReceiptId,
                    userTurnId = canonicalTurn.Receipt?.UserTurnId,
                }), ct);
            result = canonicalTurn;
        }, ct);
        return result!;
    }

    public async Task<ImportLegacyOrchestratorChatResponse> ImportLegacyOrchestratorChatAsync(
        string projectIdentity,
        ImportLegacyOrchestratorChatRequest request,
        string actorId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSha256))
            throw new ArgumentException("A source SHA-256 is required.");
        var imported = 0;
        var present = 0;
        var rejected = 0;
        var context = await EnsureOrchestratorContextAsync(projectIdentity, null, actorId, ct);
        var knownTurnIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, """
            SELECT turn_id
              FROM orchestrator_context_turns
             WHERE context_key = $context;
            """, ("$context", context.ContextKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) knownTurnIds.Add(reader.GetString(0));
        }
        foreach (var turn in request.Turns.OrderBy(item => item.CreatedAt))
        {
            try
            {
                if (knownTurnIds.Contains(turn.TurnId))
                {
                    present++;
                    continue;
                }
                await AppendOrchestratorContextTurnAsync(
                    projectIdentity,
                    null,
                    new AppendOrchestratorContextTurnRequest(turn with { Receipt = null }),
                    actorId,
                    ct);
                knownTurnIds.Add(turn.TurnId);
                imported++;
            }
            catch (Exception exception) when (exception is ArgumentException or TaskServerConflictException)
            {
                rejected++;
            }
        }
        return new ImportLegacyOrchestratorChatResponse(
            context.ContextKey, imported, present, rejected);
    }

    private static async Task<OrchestratorContextTarget> ResolveOrchestratorContextTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectIdentity,
        string? taskIdentity,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity))
            throw new ArgumentException("Project identity is required.");
        string? projectId = null;
        string? projectName = null;
        await using (var project = Command(connection, """
            SELECT id, name FROM projects
             WHERE id = $identity OR name = $identity COLLATE NOCASE
             ORDER BY CASE WHEN id = $identity THEN 0 ELSE 1 END
             LIMIT 1;
            """, transaction, ("$identity", projectIdentity.Trim())))
        await using (var reader = await project.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                projectId = reader.GetString(0);
                projectName = reader.GetString(1);
            }
        }
        if (projectId is null || projectName is null)
            throw new KeyNotFoundException($"Project '{projectIdentity}' was not found.");

        if (string.IsNullOrWhiteSpace(taskIdentity))
            return new OrchestratorContextTarget(
                $"project:{projectName}", OrchestratorContextKinds.Project,
                projectId, projectName, null, null, null, null,
                $"Project chat for {projectName}", null);

        string? taskId = null;
        string? taskKey = null;
        string? taskTitle = null;
        string? taskState = null;
        await using (var task = Command(connection, """
            SELECT id, task_key, title, state FROM tasks
             WHERE project_id = $project
               AND (id = $identity OR task_key = upper($identity))
             LIMIT 1;
            """, transaction, ("$project", projectId), ("$identity", taskIdentity.Trim())))
        await using (var reader = await task.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                taskId = reader.GetString(0);
                taskKey = reader.GetString(1);
                taskTitle = reader.GetString(2);
                taskState = reader.GetString(3);
            }
        }
        if (taskId is null || taskKey is null)
            throw new KeyNotFoundException(
                $"Task '{taskIdentity}' was not found in project '{projectIdentity}'.");
        return new OrchestratorContextTarget(
            $"task:{projectName}/{taskKey}", OrchestratorContextKinds.Task,
            projectId, projectName, taskId, taskKey, null, null,
            string.IsNullOrWhiteSpace(taskTitle) ? taskKey : taskTitle!, taskState);
    }

    private static async Task<OrchestratorContextTarget> ResolveDossierOrchestratorContextTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectIdentity,
        string dossierIdentity,
        EnsureDossierOrchestratorContextRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dossierIdentity)
            || dossierIdentity != dossierIdentity.Trim()
            || dossierIdentity.IndexOfAny(['/', '\\']) >= 0
            || dossierIdentity.Any(char.IsControl))
            throw new ArgumentException("Dossier identity must be one non-empty route segment.");
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("Dossier title is required.");
        if (request.LifecycleState is not (
            "active" or "decision-pending" or "decided" or "documented" or "archived" or
            "in-progress" or "review-requested" or "done"))
            throw new ArgumentException("Dossier lifecycle state is invalid.");

        var project = await ResolveOrchestratorContextTargetAsync(
            connection, transaction, projectIdentity, taskIdentity: null, ct);
        return new OrchestratorContextTarget(
            $"dossier:{project.ProjectName}/{dossierIdentity}",
            OrchestratorContextKinds.Dossier,
            project.ProjectId,
            project.ProjectName,
            null,
            null,
            dossierIdentity,
            string.IsNullOrWhiteSpace(request.DossierKey) ? null : request.DossierKey.Trim(),
            request.Title.Trim(),
            request.LifecycleState);
    }

    private async Task<OrchestratorContextDto> EnsureOrchestratorContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OrchestratorContextTarget target,
        string actorId,
        CancellationToken ct)
    {
        var now = UtcNow;
        var hiddenAt = OrchestratorContextVisibilityPolicy.IsHidden(target.Kind, target.LifecycleState)
            ? now
            : (DateTime?)null;
        await ExecuteAsync(connection, """
            INSERT INTO orchestrator_contexts(
                context_key, kind, project_id, task_id, dossier_id, dossier_key, title, summary,
                created_at, updated_at, hidden_at)
            VALUES ($key, $kind, $project, $task, $dossier, $dossier_key, $title, $summary, $now, $now, $hidden)
            ON CONFLICT(context_key) DO UPDATE SET
                project_id = excluded.project_id,
                task_id = excluded.task_id,
                dossier_id = excluded.dossier_id,
                dossier_key = excluded.dossier_key,
                title = excluded.title,
                hidden_at = CASE
                    WHEN excluded.hidden_at IS NULL THEN NULL
                    ELSE COALESCE(orchestrator_contexts.hidden_at, excluded.hidden_at)
                END;
            """, ct, transaction,
            ("$key", target.ContextKey),
            ("$kind", target.Kind),
            ("$project", target.ProjectId),
            ("$task", target.TaskId),
            ("$dossier", target.DossierId),
            ("$dossier_key", target.DossierKey),
            ("$title", target.Title),
            ("$summary", target.InitialSummary),
            ("$now", Iso(now)),
            ("$hidden", hiddenAt is null ? null : Iso(hiddenAt.Value)));

        await using var command = Command(connection, """
            SELECT c.context_key, c.kind, c.project_id, p.name, c.task_id, t.task_key,
                   c.summary, c.created_at, c.updated_at, c.hidden_at,
                   (SELECT count(*) FROM orchestrator_context_turns turn WHERE turn.context_key = c.context_key),
                   NULL, 0, 0, 0, 0,
                   c.title, c.dossier_id, c.dossier_key
              FROM orchestrator_contexts c
              JOIN projects p ON p.id = c.project_id
              LEFT JOIN tasks t ON t.id = c.task_id
             WHERE c.context_key = $key;
            """, transaction, ("$key", target.ContextKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The orchestrator context could not be materialized.");
        var result = ReadOrchestratorContext(reader);
        await AuditAsync(connection, transaction, actorId, "orchestrator-context.ensured",
            "orchestrator-context", result.ContextKey,
            JsonSerializer.Serialize(new
            {
                result.Kind,
                result.ProjectId,
                result.TaskId,
                result.DossierId,
                result.HiddenAt,
            }), ct);
        return result;
    }

    private static OrchestratorContextDto ReadOrchestratorContext(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6), Parse(reader.GetString(7)), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));

    private static OrchestratorContextTurnDto ReadOrchestratorContextTurn(SqliteDataReader reader)
    {
        var usage = reader.GetInt64(5) == 0 && reader.GetInt64(6) == 0
                    && reader.GetInt64(7) == 0 && reader.GetInt64(8) == 0
            ? null
            : new OrchestratorContextTokenUsageDto(
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
        return new OrchestratorContextTurnDto(
            reader.GetString(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            usage,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11)
                ? null
                : JsonSerializer.Deserialize<IReadOnlyList<OrchestratorContextAttachmentDto>>(reader.GetString(11)),
            reader.IsDBNull(12)
                ? null
                : JsonSerializer.Deserialize<OrchestratorContextReceiptDto>(reader.GetString(12)));
    }

    private static void ValidateOrchestratorContextTurn(OrchestratorContextTurnDto turn)
    {
        if (string.IsNullOrWhiteSpace(turn.TurnId)
            || turn.TurnId.Length > 80
            || turn.TurnId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Turn id must contain only letters, digits, '-' or '_'.");
        if (turn.Role is not ("user" or "orchestrator"))
            throw new ArgumentException("Turn role must be 'user' or 'orchestrator'.");
        if (turn.Body.Length > 1_000_000)
            throw new ArgumentException("Turn body exceeds the 1,000,000 character limit.");
        if (turn.Attachments?.Any(item => string.IsNullOrWhiteSpace(item.RelativePath)
                                         || item.RelativePath.Contains("..", StringComparison.Ordinal)) == true)
            throw new ArgumentException("Attachment references must be bounded relative paths.");
    }

    private static string BuildContextSummary(string body, string fallback)
    {
        var compact = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(compact)) return fallback;
        return compact.Length <= ContextSummaryMaxLength
            ? compact
            : compact[..(ContextSummaryMaxLength - 3)].TrimEnd() + "...";
    }

    private static string TurnPayloadSha(OrchestratorContextTurnDto turn)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(turn)))).ToLowerInvariant();

    private sealed record OrchestratorContextTarget(
        string ContextKey,
        string Kind,
        string ProjectId,
        string ProjectName,
        string? TaskId,
        string? TaskKey,
        string? DossierId,
        string? DossierKey,
        string InitialSummary,
        string? LifecycleState)
    {
        public string? Title => Kind == OrchestratorContextKinds.Dossier ? InitialSummary : null;
    }
}
