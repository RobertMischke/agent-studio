using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Appends one row per over-budget reroute to <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>.
/// Pairs with <see cref="StaleProgressArchiver"/>'s <c>orphan-recoveries.jsonl</c>:
/// that one records the boot-time sweep, this one records the live pickup
/// loop giving up on a 3-progress folder after the configured retry budget.
///
/// <para>ADR-0051 (failed-pickup elimination, supersedes ADR-0028/0029): an
/// over-budget folder is no longer dead-lettered to <c>3a-failed-pickup</c>. It
/// routes by cause - a spawn failure returns the task to <c>2-ready</c> (row
/// kind <see cref="PickupFailureKinds.RequeuedReady"/>) and pauses the runner;
/// a task-shaped silence or session-less zombie escalates to
/// <c>5-human-review</c> (row kind
/// <see cref="PickupFailureKinds.EscalatedHumanReview"/>). The legacy
/// <see cref="PickupFailureKinds.PickupFailed"/> kind and the
/// <c>BuildArchiveSlug</c> helper survive only for reading old on-disk rows and
/// draining historical lane contents.</para>
///
/// <para>Schema: <c>docs/system/schemas/pickup-failure.schema.json</c>.</para>
/// </summary>
public sealed class PickupFailureLog
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PickupFailureLog> _logger;
    private readonly IJsonlAppender _appender;

    public PickupFailureLog(IConfiguration configuration, ILogger<PickupFailureLog> logger, IJsonlAppender? appender = null)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
    }

    public void Append(PickupFailureRecord record)
    {
        AppendInternal(record, record.Slug);
    }

    /// <summary>
    /// Append one <see cref="PickupRestoreRecord"/> row to
    /// <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>. Same file as the
    /// dead-letter rows so the operator has a single forensics stream per
    /// workspace for the failed-pickup lifecycle (dead-letter on one line,
    /// restore on a later line, same slug); the <c>kind</c> field
    /// disambiguates which is which.
    /// </summary>
    public void AppendRestore(PickupRestoreRecord record)
    {
        AppendInternal(record, record.Slug);
    }

    private void AppendInternal(object record, string slugForDiagnostics)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "PickupFailureLog: TaskRepository not configured; skipping pickup-failures.jsonl entry for {Slug}.",
                slugForDiagnostics);
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "pickup-failures.jsonl");
            _appender.AppendAsync(path, record, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PickupFailureLog: failed to append pickup-failures.jsonl for {Slug}", slugForDiagnostics);
        }
    }

    /// <summary>
    /// Inverse of <see cref="BuildArchiveSlug"/>. Strips the
    /// <c>-pickup-failed-&lt;yyyy-mm-dd&gt;</c> tail (and an optional
    /// numeric collision suffix) from a dead-letter slug and returns the
    /// original slug. Returns <c>false</c> when the input does not match
    /// the dead-letter shape, so the caller can decide whether to treat
    /// that as a 404 or fall back to the dead-letter slug itself.
    /// </summary>
    public static bool TryParseFailedPickupSlug(string slug, out string originalSlug)
    {
        originalSlug = "";
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var match = FailedPickupSlugRegex.Match(slug);
        if (!match.Success) return false;
        originalSlug = match.Groups["original"].Value;
        return originalSlug.Length > 0;
    }

    /// <summary>
    /// Matches the slug shape produced by <see cref="BuildArchiveSlug"/>:
    /// <c>&lt;original&gt;-pickup-failed-&lt;yyyy-mm-dd&gt;</c> with an
    /// optional <c>-&lt;N&gt;</c> collision suffix.
    /// </summary>
    private static readonly Regex FailedPickupSlugRegex = new(
        @"^(?<original>.+?)-pickup-failed-\d{4}-\d{2}-\d{2}(?:-\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Disambiguating destination slug for the dead-letter move. Format:
    /// <c>&lt;original&gt;-pickup-failed-&lt;yyyy-mm-dd&gt;</c>, with a numeric
    /// suffix on collisions. ADR-0028: destination is
    /// <see cref="AgentStudio.Shared.TaskStates.FailedPickup"/>; the
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
/// <remarks>Schema: <c>docs/system/schemas/pickup-failure.schema.json</c>.</remarks>
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
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// One row in <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c> that
/// records the inverse of a dead-letter: an operator restored a folder
/// from <c>3a-failed-pickup</c> back into a live lane (usually
/// <c>2-ready</c>) via <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>.
/// The two row shapes share the file and are disambiguated by
/// <c>kind</c>; see <c>docs/system/schemas/pickup-failure.schema.json</c>.
/// </summary>
public sealed record PickupRestoreRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = PickupFailureKinds.PickupRestored;
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    /// <summary>The original (restored) slug, e.g. <c>foo</c>.</summary>
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    /// <summary>The dead-letter slug the folder was restored from, e.g. <c>foo-pickup-failed-2026-05-08</c>.</summary>
    [JsonPropertyName("sourceSlug")] public string SourceSlug { get; init; } = "";
    /// <summary>The slug the folder ended up under after the restore (equal to <c>slug</c> unless <c>keepDeadLetterSlug</c> was set).</summary>
    [JsonPropertyName("restoredAs")] public string RestoredAs { get; init; } = "";
    /// <summary>Target lane the folder was restored into (usually <c>2-ready</c>).</summary>
    [JsonPropertyName("targetState")] public string TargetState { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="PickupFailureRecord.Kind"/>
/// and <see cref="PickupRestoreRecord.Kind"/>.</summary>
public static class PickupFailureKinds
{
    public const string PickupFailed = "pickup-failed";
    public const string PickupRestored = "pickup-restored";

    /// <summary>An over-budget folder whose CLI never spawned was requeued to
    /// <c>2-ready</c> (the task is sound; the runner pauses until the CLI is
    /// fixed). Replaces the old spawn-failure dead-letter verdict.</summary>
    public const string RequeuedReady = "requeued-ready";

    /// <summary>An over-budget folder whose CLI did spawn but never produced
    /// output, or a session-less zombie that exhausted its resume budget, was
    /// escalated to <c>5-human-review</c>. Replaces the old task-shaped /
    /// zombie dead-letter verdict.</summary>
    public const string EscalatedHumanReview = "escalated-human-review";
}

/// <summary>
/// One folder on disk under a project's <c>3-progress</c> lane, paired with
/// its measured mtime and (when available) its parsed <see cref="AgentStudio.Shared.TaskInfo"/>.
/// Used by the strict-iteration progress-first picker; the picker walks
/// these oldest-first by <see cref="Mtime"/>.
/// </summary>
public sealed record ProgressPickupCandidate(
    string FolderPath,
    string Slug,
    AgentStudio.Shared.TaskInfo? Info,
    DateTime Mtime);
