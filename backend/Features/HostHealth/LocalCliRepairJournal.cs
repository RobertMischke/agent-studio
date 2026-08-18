using System.Text.Json;
using System.Text.Json.Serialization;

using AgentStudio.Persistence;

namespace AgentStudio.HostHealth;

/// <summary>
/// One row in <c>&lt;workspace&gt;/logs/cli-repairs.jsonl</c>. Sits next to
/// <c>infra-halts.jsonl</c> and <c>pickup-failures.jsonl</c>: same home, same
/// shape, different signal. This is the record that makes the auto-update
/// hypothesis provable, so it carries the CLI version on both sides of the
/// repair plus the npm activity that overlapped the breakage.
/// </summary>
public sealed record LocalCliRepairRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("cliType")] public string CliType { get; init; } = "";
    [JsonPropertyName("packageId")] public string PackageId { get; init; } = "";

    /// <summary>Diagnosed state, e.g. <c>ShimMissingPackagePresent</c>.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";

    /// <summary>Action the diagnosis licensed, e.g. <c>GlobalReinstall</c>.</summary>
    [JsonPropertyName("action")] public string Action { get; init; } = "";

    /// <summary>One-line operator-facing explanation of the state.</summary>
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";

    /// <summary>True when the repair ran; false when it was throttled or not licensed.</summary>
    [JsonPropertyName("attempted")] public bool Attempted { get; init; }

    /// <summary>True when the CLI answered <c>--version</c> after the repair.</summary>
    [JsonPropertyName("repaired")] public bool Repaired { get; init; }

    /// <summary>Set when an automatic attempt was suppressed by the one-per-window rate limit.</summary>
    [JsonPropertyName("throttledReason")] public string? ThrottledReason { get; init; }

    /// <summary>True when a human asked for this repair instead of the periodic probe.</summary>
    [JsonPropertyName("operatorRequested")] public bool OperatorRequested { get; init; }

    /// <summary>CLI version before the repair; null when the CLI would not run.</summary>
    [JsonPropertyName("versionBefore")] public string? VersionBefore { get; init; }

    /// <summary>CLI version after the repair; the other half of the auto-update evidence.</summary>
    [JsonPropertyName("versionAfter")] public string? VersionAfter { get; init; }

    /// <summary>Installed package version read from <c>package.json</c> before the repair.</summary>
    [JsonPropertyName("packageVersionBefore")] public string? PackageVersionBefore { get; init; }

    /// <summary>Installed package version after the repair.</summary>
    [JsonPropertyName("packageVersionAfter")] public string? PackageVersionAfter { get; init; }

    [JsonPropertyName("durationMs")] public double? DurationMs { get; init; }

    /// <summary>npm debug logs that overlap the breakage window: file names and mtimes, never contents.</summary>
    [JsonPropertyName("npmActivity")] public IReadOnlyList<LocalCliRepairNpmActivity> NpmActivity { get; init; } = [];

    /// <summary>Tail of npm's own output when a repair ran; null otherwise.</summary>
    [JsonPropertyName("installerOutput")] public string? InstallerOutput { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>One npm debug log observed inside the breakage window.</summary>
public sealed record LocalCliRepairNpmActivity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>
/// Appends repair rows to <c>&lt;workspace&gt;/logs/cli-repairs.jsonl</c>.
/// Best-effort by design: a workspace that is not configured, or a disk that
/// refuses the append, must never turn a successful repair into a failure.
/// </summary>
public sealed class LocalCliRepairJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliRepairJournal> _logger;
    private readonly IJsonlAppender _appender;

    public LocalCliRepairJournal(
        IConfiguration configuration,
        ILogger<LocalCliRepairJournal> logger,
        IJsonlAppender? appender = null)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
    }

    public async Task AppendAsync(LocalCliRepairRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "LocalCliRepairJournal: TaskRepository not configured; skipping cli-repairs.jsonl entry for {CliType}.",
                record.CliType);
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "cli-repairs.jsonl");
            await _appender.AppendAsync(path, record, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalCliRepairJournal: failed to append cli-repairs.jsonl for {CliType}", record.CliType);
        }
    }
}
