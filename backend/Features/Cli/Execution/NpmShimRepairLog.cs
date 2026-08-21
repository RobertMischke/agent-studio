using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Cli;

/// <summary>
/// Durable, root-cause-provable record of every <see cref="NpmShimHealer.TryHealClaudeAsync"/>
/// pass to <c>&lt;workspace&gt;/logs/npm-shim-repairs.jsonl</c> - same shape and home as
/// <c>InfraHaltLog</c> (<c>logs/infra-halts.jsonl</c>) and <c>PickupFailureLog</c>
/// (<c>logs/pickup-failures.jsonl</c>). <see cref="NpmShimHealer"/> is a static helper with no
/// DI container, so this journal is static too and takes its dependencies as parameters rather
/// than through injected fields.
/// </summary>
public static class NpmShimRepairLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Append one repair-pass outcome. Best-effort: an IO failure or missing
    /// <c>workspaceRoot</c> is logged and swallowed, matching every other JSONL journal in
    /// this codebase - a failed audit write must never fail the repair itself.</summary>
    public static void Append(
        string? workspaceRoot,
        string cliType,
        HealOutcome outcome,
        DateTime at,
        ILogger logger,
        IJsonlAppender? appender = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            logger.LogDebug("NpmShimRepairLog: workspace root not configured; skipping npm-shim-repairs.jsonl entry");
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "npm-shim-repairs.jsonl");
            var record = new NpmShimRepairRecord
            {
                At = at,
                CliType = cliType,
                Diagnosis = outcome.Diagnosis,
                Available = outcome.Available,
                Actions = outcome.Actions,
                VersionBefore = outcome.VersionBefore,
                VersionAfter = outcome.VersionAfter,
                NpmInstallAttempted = outcome.NpmInstallAttempted,
                NpmInstallThrottled = outcome.NpmInstallThrottled,
                Error = outcome.Error,
            };
            (appender ?? new JsonlAppender()).AppendAsync(path, record, JsonOptions).GetAwaiter().GetResult();

            // Non-silent per AGT-2673 requirement 2: a repair is never just a log line
            // nobody reads by default, but it also never spams at Warning when nothing
            // needed fixing. Warn only when the pass leaves the CLI unavailable.
            if (!outcome.Available)
            {
                logger.LogWarning(
                    "[npm-shim-repair] {Cli} still unavailable after repair pass ({Diagnosis}): {Error}",
                    cliType, outcome.Diagnosis, outcome.Error);
            }
            else if (outcome.Diagnosis != NpmShimHealDiagnosis.Healthy)
            {
                logger.LogInformation(
                    "[npm-shim-repair] {Cli} repaired at {At:O} ({Diagnosis}, npmInstall={NpmInstallAttempted}): {Actions}",
                    cliType, at, outcome.Diagnosis, outcome.NpmInstallAttempted, string.Join("; ", outcome.Actions));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NpmShimRepairLog: failed to append npm-shim-repairs.jsonl for {Cli}", cliType);
        }
    }

    /// <summary>Best-effort read of the most recent journal entry for <paramref name="cliType"/>,
    /// for read-only surfaces (e.g. the CLI paths panel) that want to show "last repaired at
    /// &lt;time&gt;" without re-running the healer. Returns null when no journal exists yet or
    /// nothing was ever recorded for this CLI.</summary>
    public static NpmShimRepairRecord? ReadLast(string? workspaceRoot, string cliType)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        var path = Path.Combine(workspaceRoot, "logs", "npm-shim-repairs.jsonl");
        if (!File.Exists(path)) return null;

        NpmShimRepairRecord? last = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                NpmShimRepairRecord? record;
                try { record = JsonSerializer.Deserialize<NpmShimRepairRecord>(line, JsonOptions); }
                catch (JsonException) { continue; }
                if (record is not null && string.Equals(record.CliType, cliType, StringComparison.OrdinalIgnoreCase))
                    last = record;
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "NpmShimRepairLog: read is best-effort"); }
        return last;
    }
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/npm-shim-repairs.jsonl</c>.</summary>
public sealed record NpmShimRepairRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("cliType")] public string CliType { get; init; } = "";
    [JsonPropertyName("diagnosis")] public string Diagnosis { get; init; } = "";
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("actions")] public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    [JsonPropertyName("versionBefore")] public string? VersionBefore { get; init; }
    [JsonPropertyName("versionAfter")] public string? VersionAfter { get; init; }
    [JsonPropertyName("npmInstallAttempted")] public bool NpmInstallAttempted { get; init; }
    [JsonPropertyName("npmInstallThrottled")] public bool NpmInstallThrottled { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
