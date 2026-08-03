using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// Owns the durable <c>lifecycle.json</c> projection at coding and Post
/// Processing attempt boundaries. Each new attempt replaces stale active
/// checks, and every terminal boundary closes active checks with a timestamp.
/// </summary>
internal static class PostProcessingLifecycleStore
{
    private static readonly ConcurrentDictionary<string, object> FileLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static bool ResetForExecution(
        string folderPath,
        DateTime startedAtUtc,
        ILogger logger)
        => Update(folderPath, logger, snapshot => snapshot with
        {
            Phase = LifecyclePhases.ExecutionRunning,
            PhaseEnteredAt = Utc(startedAtUtc),
            BlockingReason = null,
            PostProcessingChecks = [],
        });

    internal static bool BeginPostProcessing(
        string folderPath,
        DateTime startedAtUtc,
        string checkName,
        string detail,
        ILogger logger,
        bool replaceChecks)
        => Update(folderPath, logger, snapshot =>
        {
            var at = Utc(startedAtUtc);
            var checks = replaceChecks
                ? []
                : snapshot.PostProcessingChecks
                    .Where(check => !string.Equals(check.Name, checkName, StringComparison.Ordinal))
                    .ToList();
            checks.Add(new LifecycleCheck
            {
                Name = checkName,
                Status = LifecycleCheckStatuses.Running,
                StartedAt = at,
                Detail = detail,
            });
            return snapshot with
            {
                Phase = LifecyclePhases.PostProcessingRunning,
                PhaseEnteredAt = at,
                BlockingReason = null,
                PostProcessingChecks = checks,
            };
        });

    internal static bool Terminalize(
        string folderPath,
        DateTime finishedAtUtc,
        bool failed,
        string detail,
        ILogger logger,
        bool onlyWhenActive = false)
        => Update(folderPath, logger, snapshot =>
        {
            var active = snapshot.PostProcessingChecks.Any(IsActive)
                || string.Equals(
                    snapshot.Phase,
                    LifecyclePhases.PostProcessingRunning,
                    StringComparison.OrdinalIgnoreCase);
            if (onlyWhenActive && !active) return null;

            var at = Utc(finishedAtUtc);
            var terminalStatus = failed
                ? LifecycleCheckStatuses.Failed
                : LifecycleCheckStatuses.Completed;
            var checks = snapshot.PostProcessingChecks
                .Select(check => IsActive(check)
                    ? check with
                    {
                        Status = terminalStatus,
                        StartedAt = check.StartedAt ?? at,
                        FinishedAt = at,
                        Detail = detail,
                    }
                    : check)
                .ToList();
            return snapshot with
            {
                Phase = failed
                    ? LifecyclePhases.PostProcessingBlocked
                    : LifecyclePhases.AwaitingReview,
                PhaseEnteredAt = at,
                BlockingReason = failed ? detail : null,
                PostProcessingChecks = checks,
            };
        });

    private static bool IsActive(LifecycleCheck check)
        => string.Equals(check.Status, LifecycleCheckStatuses.Pending, StringComparison.OrdinalIgnoreCase)
           || string.Equals(check.Status, LifecycleCheckStatuses.Running, StringComparison.OrdinalIgnoreCase);

    private static bool Update(
        string folderPath,
        ILogger logger,
        Func<LifecycleSnapshot, LifecycleSnapshot?> update)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return false;
        var path = Path.Combine(folderPath, "lifecycle.json");
        var gate = FileLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());
        lock (gate)
        {
            try
            {
                var snapshot = Read(path) ?? new LifecycleSnapshot();
                var updated = update(snapshot);
                if (updated is null) return false;
                File.WriteAllText(path, JsonSerializer.Serialize(updated, WriteOptions));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update Post Processing lifecycle at {Path}", path);
                return false;
            }
        }
    }

    private static LifecycleSnapshot? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LifecycleSnapshot>(File.ReadAllText(path), ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

internal static class LifecycleCheckStatuses
{
    internal const string Pending = "pending";
    internal const string Running = "running";
    internal const string Completed = "completed";
    internal const string Failed = "failed";
}
