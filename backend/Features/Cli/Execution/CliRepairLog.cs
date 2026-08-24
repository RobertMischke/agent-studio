using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Cli;

/// <summary>
/// Appends one row per npm-shim repair attempt (success or failure, never
/// silent) to <c>&lt;workspace&gt;/logs/cli-repairs.jsonl</c>. Pairs with
/// <c>AgentStudio.Runner.InfraHaltLog</c> and <c>PickupFailureLog</c> - same
/// shape, different signal. The before/after version pair is the evidence
/// that lets an operator confirm or rule out "an auto-update swapped the
/// install under us" for a given local-host CLI outage.
/// </summary>
public sealed class CliRepairLog
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly AgentStudio.Persistence.IJsonlAppender _appender;

    public CliRepairLog(
        IConfiguration configuration,
        ILogger logger,
        AgentStudio.Persistence.IJsonlAppender? appender = null)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender ?? new AgentStudio.Persistence.JsonlAppender();
    }

    public void Append(CliRepairRecord record)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "CliRepairLog: TaskRepository not configured; skipping cli-repairs.jsonl entry for {Cli}.",
                record.Cli);
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "cli-repairs.jsonl");
            _appender.AppendAsync(path, record, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CliRepairLog: failed to append cli-repairs.jsonl for {Cli}", record.Cli);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/cli-repairs.jsonl</c>.</summary>
public sealed record CliRepairRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("cli")] public string Cli { get; init; } = "";
    [JsonPropertyName("packagePresent")] public bool PackagePresent { get; init; }
    [JsonPropertyName("actions")] public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("versionBefore")] public string? VersionBefore { get; init; }
    [JsonPropertyName("versionAfter")] public string? VersionAfter { get; init; }
}
