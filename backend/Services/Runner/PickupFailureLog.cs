using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Appends one row per dead-letter to <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>.
/// Pairs with <see cref="StaleProgressArchiver"/>'s <c>orphan-recoveries.jsonl</c>:
/// that one records the boot-time sweep, this one records the live pickup
/// loop giving up on a 3-progress folder after the configured retry budget.
///
/// <para>ADR-0028: dead-letter destination is <c>3a-failed-pickup</c>, not
/// <c>7-archive</c>. The slug builder name is unchanged for log-format
/// continuity but the destination state is the visible failure lane.</para>
///
/// <para>Schema: <c>docs/schemas/pickup-failure.schema.json</c>.</para>
/// </summary>
public sealed class PickupFailureLog
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PickupFailureLog> _logger;

    public PickupFailureLog(IConfiguration configuration, ILogger<PickupFailureLog> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void Append(PickupFailureRecord record)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "PickupFailureLog: TaskRepository not configured; skipping pickup-failures.jsonl entry for {Slug}.",
                record.Slug);
            return;
        }

        try
        {
            var dir = Path.Combine(workspaceRoot, "logs");
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(record, JsonOptions);
            File.AppendAllText(Path.Combine(dir, "pickup-failures.jsonl"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PickupFailureLog: failed to append pickup-failures.jsonl for {Slug}", record.Slug);
        }
    }

    /// <summary>
    /// Disambiguating destination slug for the dead-letter move. Format:
    /// <c>&lt;original&gt;-pickup-failed-&lt;yyyy-mm-dd&gt;</c>, with a numeric
    /// suffix on collisions. ADR-0028: destination is
    /// <see cref="OrchestratorApi.Models.JobStates.FailedPickup"/>; the
    /// existence-check callback is parameterised so the runner can inject the
    /// FailedPickup-folder check. Pure helper so tests can pin the format.
    /// </summary>
    public static string BuildArchiveSlug(string slug, DateTime utcNow, Func<string, bool> existsInDestination)
    {
        var datePart = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var baseSlug = $"{slug}-pickup-failed-{datePart}";
        var attempt = baseSlug;
        for (int i = 2; existsInDestination(attempt) && i < 1000; i++)
        {
            attempt = $"{baseSlug}-{i}";
        }
        return attempt;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>.</summary>
/// <remarks>Schema: <c>docs/schemas/pickup-failure.schema.json</c>.</remarks>
public sealed record PickupFailureRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = PickupFailureKinds.PickupFailed;
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    /// <summary>Disambiguated slug under <c>3a-failed-pickup</c> after the dead-letter move (ADR-0028).</summary>
    [JsonPropertyName("destinationSlug")] public string DestinationSlug { get; init; } = "";
    [JsonPropertyName("attempts")] public int Attempts { get; init; }
    [JsonPropertyName("threshold")] public int Threshold { get; init; }
    [JsonPropertyName("outputDeadlineSeconds")] public int OutputDeadlineSeconds { get; init; }
    [JsonPropertyName("attemptHistory")] public IReadOnlyList<PickupAttemptDiagnostic>? AttemptHistory { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>One per-attempt diagnostic inside <see cref="PickupFailureRecord.AttemptHistory"/>.</summary>
public sealed record PickupAttemptDiagnostic
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("outputLines")] public int OutputLines { get; init; }
    [JsonPropertyName("executionStatus")] public string? ExecutionStatus { get; init; }
}

/// <summary>String constants for <see cref="PickupFailureRecord.Kind"/>.</summary>
public static class PickupFailureKinds
{
    public const string PickupFailed = "pickup-failed";
}

/// <summary>
/// One folder on disk under a project's <c>3-progress</c> lane, paired with
/// its measured mtime and (when available) its parsed <see cref="OrchestratorApi.Models.JobInfo"/>.
/// Used by the strict-iteration progress-first picker; the picker walks
/// these oldest-first by <see cref="Mtime"/>.
/// </summary>
public sealed record ProgressPickupCandidate(
    string FolderPath,
    string Slug,
    OrchestratorApi.Models.JobInfo? Info,
    DateTime Mtime);
