using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Application boundary for centrally managed execution-host capacity.
/// Capacity is host-scoped: every project claimed on a host consumes the same
/// ceiling, while project identity remains an admission/query dimension only.
/// </summary>
public sealed class RuntimeCapacitySettingsService(TaskServerStore store)
{
    public Task<RuntimeCapacitySettingsDto?> GetAsync(
        string hostId,
        CancellationToken cancellationToken = default)
        => store.GetRuntimeCapacitySettingsAsync(hostId, cancellationToken);

    public Task<RuntimeCapacitySettingsDto> UpdateAsync(
        string hostId,
        UpdateRuntimeCapacitySettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
        => store.UpdateRuntimeCapacitySettingsAsync(
            hostId,
            request,
            actorId,
            cancellationToken);
}

public sealed partial class TaskServerStore
{
    public async Task<RuntimeCapacitySettingsDto?> GetRuntimeCapacitySettingsAsync(
        string hostId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        await using var connection = await OpenReadyAsync(ct);
        return await ReadRuntimeCapacitySettingsAsync(
            connection,
            null,
            hostId.Trim(),
            ct);
    }

    public async Task<RuntimeCapacitySettingsDto> UpdateRuntimeCapacitySettingsAsync(
        string hostId,
        UpdateRuntimeCapacitySettingsRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        ValidateRuntimeCapacity(request);
        var normalizedHostId = hostId.Trim();
        RuntimeCapacitySettingsDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadRuntimeCapacitySettingsAsync(
                connection,
                transaction,
                normalizedHostId,
                ct);
            if (existing is null)
                throw new KeyNotFoundException("Execution host capacity was not found.");
            if (existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected runtime capacity version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = UtcNow;
            var affected = await ExecuteAsync(connection, """
                UPDATE runtime_capacity_settings
                   SET max_parallelism = $max,
                       target_load_percent = $target,
                       ramp_strategy = $ramp,
                       version = version + 1,
                       updated_at = $now
                 WHERE host_id = $host AND version = $expected;
                """, ct, transaction,
                ("$max", request.MaxParallelism),
                ("$target", request.TargetLoadPercent),
                ("$ramp", NormalizeRampStrategy(request.RampStrategy)),
                ("$now", Iso(now)),
                ("$host", normalizedHostId),
                ("$expected", request.ExpectedVersion));
            if (affected != 1)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    "Runtime capacity changed since it was read.");

            updated = new RuntimeCapacitySettingsDto(
                normalizedHostId,
                request.MaxParallelism,
                request.TargetLoadPercent,
                NormalizeRampStrategy(request.RampStrategy),
                request.ExpectedVersion + 1,
                now);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "runtime-capacity.updated",
                "host",
                normalizedHostId,
                JsonSerializer.Serialize(new
                {
                    request.MaxParallelism,
                    request.TargetLoadPercent,
                    rampStrategy = NormalizeRampStrategy(request.RampStrategy),
                    request.ExpectedVersion,
                    updated.Version,
                }),
                ct);
        }, ct);
        return updated!;
    }

    private static async Task<RuntimeCapacitySettingsDto?> ReadRuntimeCapacitySettingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT host_id, max_parallelism, target_load_percent, ramp_strategy,
                   version, updated_at
              FROM runtime_capacity_settings
             WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new RuntimeCapacitySettingsDto(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                Parse(reader.GetString(5)))
            : null;
    }

    private static async Task<int> CountOccupiedHostSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hostId,
        CancellationToken ct)
        => Convert.ToInt32(
            await ScalarAsync(connection, """
                SELECT COUNT(*)
                  FROM leases l
                  JOIN runners r ON r.id = l.runner_id
                 WHERE r.host_id = $host
                   AND l.status IN ('active', 'process-unknown');
                """, ct, transaction, ("$host", hostId)) ?? 0,
            CultureInfo.InvariantCulture);

    private async Task RecordRuntimeCapacityAdoptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        RuntimeCapacitySettingsDto current,
        int? reportedMaxParallelism,
        long? reportedVersion,
        DateTime? reportedAppliedAt,
        string actorId,
        CancellationToken ct)
    {
        var validMaxParallelism = reportedMaxParallelism is >= 1 and <= 256
            ? reportedMaxParallelism
            : null;
        long? exactVersion = reportedVersion == current.Version
                             && validMaxParallelism == current.MaxParallelism
            ? reportedVersion
            : null;
        var previousVersionValue = await ScalarAsync(
            connection,
            "SELECT runtime_capacity_applied_version FROM runners WHERE id = $runner;",
            ct,
            transaction,
            ("$runner", runnerId));
        long? previousVersion = previousVersionValue is null or DBNull
            ? null
            : Convert.ToInt64(previousVersionValue, CultureInfo.InvariantCulture);
        var appliedAt = NormalizeReportedAppliedAt(reportedAppliedAt);

        await ExecuteAsync(connection, """
            UPDATE runners
               SET last_seen_at = $now,
                   effective_max_parallelism = COALESCE($effective, effective_max_parallelism),
                   runtime_capacity_applied_at = CASE
                       WHEN $effective IS NOT NULL THEN $applied
                       ELSE runtime_capacity_applied_at
                   END,
                   runtime_capacity_applied_version = $version
             WHERE id = $runner;
            """, ct, transaction,
            ("$now", Iso(UtcNow)),
            ("$effective", validMaxParallelism),
            ("$applied", Iso(appliedAt)),
            ("$version", exactVersion),
            ("$runner", runnerId));

        if (exactVersion is null || previousVersion == exactVersion) return;
        await AuditAsync(
            connection,
            transaction,
            actorId,
            "runtime-capacity.applied",
            "runner",
            runnerId,
            JsonSerializer.Serialize(new
            {
                current.HostId,
                settingsVersion = exactVersion,
                current.MaxParallelism,
                appliedAt,
            }),
            ct);
    }

    private DateTime NormalizeReportedAppliedAt(DateTime? value)
    {
        var now = UtcNow;
        if (value is null) return now;
        var normalized = value.Value.ToUniversalTime();
        return normalized <= now.AddMinutes(2) ? normalized : now;
    }

    private static void ValidateRuntimeCapacity(
        UpdateRuntimeCapacitySettingsRequest request)
    {
        if (request.MaxParallelism is < 1 or > 256)
            throw new ArgumentException("Runtime capacity maxParallelism must be between 1 and 256.");
        if (request.TargetLoadPercent is < 50 or > 95)
            throw new ArgumentException("Runtime capacity targetLoadPercent must be between 50 and 95.");
        if (request.ExpectedVersion < 1)
            throw new ArgumentException("Runtime capacity expectedVersion must be positive.");
        _ = NormalizeRampStrategy(request.RampStrategy);
    }

    private static string NormalizeRampStrategy(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "conservative" => "conservative",
            "balanced" => "balanced",
            "aggressive" => "aggressive",
            _ => throw new ArgumentException(
                "Runtime capacity rampStrategy must be conservative, balanced, or aggressive."),
        };
}
