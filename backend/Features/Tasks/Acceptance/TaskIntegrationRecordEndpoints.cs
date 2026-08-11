using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator-reviewed append-only integration bookkeeping. This is the HTTP
/// boundary for the same record shape written by
/// <see cref="HistoricalIntegrationVerificationSweep"/>. It never changes a
/// lane, task branch, commit chain, or Git history.
/// </summary>
public static class TaskIntegrationRecordEndpoints
{
    public static void MapTaskIntegrationRecordEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration-records", (
            string jobId,
            string? project,
            string? watchPath,
            AppendTaskIntegrationRecordRequest request,
            TaskScannerService scanner,
            TaskMutationService mutations,
            ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var task = scanner.FindJob(jobId, watchPath);
            if (task is null) return Results.NotFound(new { error = "Task not found." });

            var validation = TaskIntegrationRecordAppendPolicy.Validate(task.State, request);
            if (!validation.Allowed)
            {
                return validation.InFlight
                    ? Results.Conflict(new { error = validation.Error })
                    : Results.BadRequest(new { error = validation.Error });
            }

            var record = new TaskIntegrationRecord
            {
                Id = request.Id.Trim().ToLowerInvariant(),
                Version = 1,
                Classification = request.Classification.Trim().ToLowerInvariant(),
                RecordedAtUtc = DateTime.UtcNow,
                AcceptedAtUtc = NormalizeUtc(request.AcceptedAtUtc),
                IntegrationBranch = TaskIntegrationBranch.Name(
                    string.IsNullOrWhiteSpace(request.IntegrationBranch)
                        ? settings.Get(task.ProjectName).IntegrationBranch
                        : request.IntegrationBranch),
                CommitShas = NormalizeValues(request.CommitShas, lower: true),
                FenceRefs = NormalizeValues(request.FenceRefs, lower: false),
                Evidence = request.Evidence.Trim(),
            };

            var write = mutations.AppendIntegrationRecordOnFolder(task.FolderPath, record);
            if (!write.Succeeded)
            {
                return Results.Json(
                    new { error = "Failed to append the integration record." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var persisted = scanner.FindJob(task.Id, task.WatchPath)?.IntegrationRecords
                .FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase))
                ?? record;
            return Results.Ok(new { appended = write.Appended, record = persisted });
        });
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (value is null) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };
    }

    private static List<string> NormalizeValues(IEnumerable<string>? values, bool lower)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Select(value => lower ? value.ToLowerInvariant() : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Request to append one stable integration bookkeeping row.</summary>
public sealed record AppendTaskIntegrationRecordRequest
{
    public string Id { get; init; } = "";
    public string Classification { get; init; } = "";
    public DateTime? AcceptedAtUtc { get; init; }
    public string? IntegrationBranch { get; init; }
    public List<string> CommitShas { get; init; } = [];
    public List<string> FenceRefs { get; init; } = [];
    public string Evidence { get; init; } = "";
}

/// <summary>Pure boundary validation for append-only integration records.</summary>
public static class TaskIntegrationRecordAppendPolicy
{
    private static readonly HashSet<string> AcceptedLanes = new(StringComparer.Ordinal)
    {
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    public static IntegrationRecordAppendValidation Validate(
        string state,
        AppendTaskIntegrationRecordRequest? request)
    {
        if (!AcceptedLanes.Contains(state))
        {
            return new(false, true,
                $"Integration records cannot be appended while a task is in-flight in '{state}'.");
        }

        if (request is null)
            return new(false, false, "A request body is required.");

        var id = request.Id.Trim();
        if (id.Length is < 3 or > 96 || !IsRecordId(id))
            return new(false, false, "id must be 3-96 lowercase letters, digits, or hyphens.");

        var classification = request.Classification.Trim().ToLowerInvariant();
        if (!IntegrationRecordClasses.All.Contains(classification, StringComparer.Ordinal))
            return new(false, false, "classification must use the historical integration five-class schema.");

        var evidence = request.Evidence.Trim();
        if (evidence.Length is < 8 or > 4000)
            return new(false, false, "evidence must contain 8-4000 characters.");

        if ((request.CommitShas?.Count ?? 0) > 100
            || (request.CommitShas?.Any(sha => !IsCommitSha(sha)) ?? false))
            return new(false, false, "commitShas must contain at most 100 hexadecimal SHAs of 7-40 characters.");

        if ((request.FenceRefs?.Count ?? 0) > 100
            || (request.FenceRefs?.Any(reference =>
                string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 512) ?? false))
        {
            return new(false, false, "fenceRefs must contain at most 100 non-empty refs of at most 512 characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.IntegrationBranch)
            && !IsBranchName(TaskIntegrationBranch.Name(request.IntegrationBranch)))
        {
            return new(false, false, "integrationBranch is invalid.");
        }

        return new(true, false, null);
    }

    private static bool IsRecordId(string value)
        => value.All(character => character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character == '-');

    private static bool IsCommitSha(string value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length is >= 7 and <= 40 && trimmed.All(Uri.IsHexDigit);
    }

    private static bool IsBranchName(string value)
        => value.Length is > 0 and <= 255
           && !value.StartsWith('/')
           && !value.EndsWith('/')
           && !value.EndsWith('.')
           && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
           && !value.Contains("..", StringComparison.Ordinal)
           && !value.Contains("//", StringComparison.Ordinal)
           && !value.Contains("@{", StringComparison.Ordinal)
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '-' or '_' or '.' or '/');
}

public sealed record IntegrationRecordAppendValidation(bool Allowed, bool InFlight, string? Error);
