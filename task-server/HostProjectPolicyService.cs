using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Application boundary for the projects an execution host may claim. The
/// policy is enforced by the Task Server, which already owns claim selection.
/// </summary>
public sealed class HostProjectPolicyService(TaskServerStore store)
{
    public Task<HostProjectPolicyDto?> GetAsync(
        string hostId,
        CancellationToken cancellationToken = default)
        => store.GetHostProjectPolicyAsync(hostId, cancellationToken);

    public Task<HostProjectPolicyDto> UpdateAsync(
        string hostId,
        UpdateHostProjectPolicyRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
        => store.UpdateHostProjectPolicyAsync(
            hostId,
            request,
            actorId,
            cancellationToken);
}

public sealed partial class TaskServerStore
{
    public async Task<HostProjectPolicyDto?> GetHostProjectPolicyAsync(
        string hostId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        await using var connection = await OpenReadyAsync(ct);
        return await ReadHostProjectPolicyAsync(connection, null, hostId.Trim(), ct);
    }

    public async Task<HostProjectPolicyDto> UpdateHostProjectPolicyAsync(
        string hostId,
        UpdateHostProjectPolicyRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(hostId))
            throw new ArgumentException("Host id is required.");
        if (request.ExpectedVersion < 0)
            throw new ArgumentException("Host project policy expectedVersion cannot be negative.");

        var normalizedHostId = hostId.Trim();
        var allowedProjectIds = NormalizeProjectIds(request);
        HostProjectPolicyDto? updated = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ValidateProjectsExistAsync(connection, transaction, allowedProjectIds, ct);
            var existing = await ReadHostProjectPolicyAsync(
                connection,
                transaction,
                normalizedHostId,
                ct);
            if (existing is null && request.ExpectedVersion != 0)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected host project policy version {request.ExpectedVersion}, but the host has no central policy.");
            if (existing is not null && existing.Version != request.ExpectedVersion)
                throw new TaskServerConflictException(
                    "resource-version-mismatch",
                    $"Expected host project policy version {request.ExpectedVersion}, current version is {existing.Version}.");

            var now = UtcNow;
            var nextVersion = existing is null ? 1 : existing.Version + 1;
            await ExecuteAsync(connection, """
                INSERT INTO host_project_policies(
                    host_id, allow_all_projects, version, updated_at)
                VALUES ($host, $allowAll, $version, $now)
                ON CONFLICT(host_id) DO UPDATE SET
                    allow_all_projects = excluded.allow_all_projects,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                DELETE FROM host_allowed_projects WHERE host_id = $host;
                """, ct, transaction,
                ("$host", normalizedHostId),
                ("$allowAll", request.AllowAllProjects ? 1 : 0),
                ("$version", nextVersion),
                ("$now", Iso(now)));
            foreach (var projectId in allowedProjectIds)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO host_allowed_projects(host_id, project_id)
                    VALUES ($host, $project);
                    """, ct, transaction,
                    ("$host", normalizedHostId),
                    ("$project", projectId));
            }

            updated = new HostProjectPolicyDto(
                normalizedHostId,
                request.AllowAllProjects,
                allowedProjectIds,
                nextVersion,
                now);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                existing is null ? "host-project-policy.created" : "host-project-policy.updated",
                "host",
                normalizedHostId,
                JsonSerializer.Serialize(new
                {
                    request.AllowAllProjects,
                    allowedProjectIds,
                    request.ExpectedVersion,
                    updated.Version,
                }),
                ct);
        }, ct);
        return updated!;
    }

    private static async Task<HostProjectPolicyDto?> ReadHostProjectPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        bool allowAll;
        long version;
        DateTime updatedAt;
        await using (var command = Command(connection, """
            SELECT allow_all_projects, version, updated_at
              FROM host_project_policies
             WHERE host_id = $host;
            """, transaction, ("$host", hostId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            allowAll = reader.GetInt32(0) == 1;
            version = reader.GetInt64(1);
            updatedAt = Parse(reader.GetString(2));
        }

        var allowedProjectIds = new List<string>();
        await using (var command = Command(connection, """
            SELECT project_id
              FROM host_allowed_projects
             WHERE host_id = $host
             ORDER BY project_id;
            """, transaction, ("$host", hostId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                allowedProjectIds.Add(reader.GetString(0));
        }

        return new HostProjectPolicyDto(
            hostId,
            allowAll,
            allowedProjectIds,
            version,
            updatedAt);
    }

    private static IReadOnlyList<string> NormalizeProjectIds(
        UpdateHostProjectPolicyRequest request)
    {
        var normalized = (request.AllowedProjectIds ?? [])
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Select(projectId => projectId.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(projectId => projectId, StringComparer.Ordinal)
            .ToArray();
        return request.AllowAllProjects ? [] : normalized;
    }

    private static async Task ValidateProjectsExistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> projectIds,
        CancellationToken ct)
    {
        foreach (var projectId in projectIds)
        {
            var exists = Convert.ToInt32(
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM projects WHERE id = $project;",
                    ct,
                    transaction,
                    ("$project", projectId)) ?? 0) == 1;
            if (!exists)
                throw new ArgumentException($"Project '{projectId}' was not found.");
        }
    }

}
