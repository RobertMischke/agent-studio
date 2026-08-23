using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Cli;

/// <summary>
/// Root-cause audit trail for <see cref="NpmShimHealer"/> repair passes
/// (AGT-2673). Appends one row per pre-spawn health-check outcome to
/// <c>&lt;workspace&gt;/logs/cli-self-heal.jsonl</c> - same shape and home as
/// <c>InfraHaltLog</c> (<c>logs/infra-halts.jsonl</c>) and
/// <c>StaleProgressArchiver</c> (<c>logs/orphan-recoveries.jsonl</c>).
///
/// <para>
/// A silent fix is what let the 2026-08-13 and 2026-08-18 incidents repeat
/// undetected until an operator noticed the CLI missing by hand both times.
/// This turns every repair pass - healthy no-op, successful repair, or
/// still-broken after repair - into a durable, greppable line: the version
/// jump (2.1.231 -&gt; 2.1.234) that only becomes visible with a before/after
/// snapshot, whether the shim vs. truly-uninstalled classification fired,
/// and whether the hourly cooldown gated the <c>npm install -g</c> fallback.
/// </para>
/// </summary>
public static class CliSelfHealJournal
{
    /// <summary>
    /// Best-effort append. Never throws: a journal failure must not affect
    /// whether a job can spawn. Only writes when the health check actually
    /// exercised the repair pass (skips the common "claude was already
    /// available" case to keep the file to signal, not noise).
    /// </summary>
    public static void RecordIfRepairAttempted(
        IConfiguration configuration,
        ILogger logger,
        string cliType,
        HealOutcome outcome,
        DateTime utcNow)
    {
        if (outcome.Actions.Count == 0 && outcome.Category == ShimRepairCategory.Healthy)
        {
            // Nothing ran (non-Windows, or claude was already on PATH before
            // NpmShimHealer was even called) - not repair activity worth journaling.
            return;
        }

        var workspaceRoot = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            logger.LogDebug("CliSelfHealJournal: TaskRepository not configured; skipping cli-self-heal.jsonl entry");
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "cli-self-heal.jsonl");
            var record = new CliSelfHealRecord
            {
                At = utcNow,
                CliType = cliType,
                Category = outcome.Category.ToString(),
                Healed = outcome.Available,
                Actions = outcome.Actions,
                VersionBefore = outcome.VersionBefore,
                VersionAfter = outcome.VersionAfter,
                NpmInstallAttempted = outcome.NpmInstallAttempted,
                RateLimited = outcome.RateLimited,
                Error = outcome.Error,
            };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            File.AppendAllText(path, line, System.Text.Encoding.UTF8);

            // Distinct, greppable tags so this event is visible without opening
            // the journal file - "instead of a silent fix" (AGT-2673 item 2).
            // Alarm (warning) only on a failed repair; a successful repair or
            // healthy no-op logs at information level.
            if (!outcome.Available)
            {
                logger.LogWarning(
                    "cli-self-heal-failed cli={CliType} category={Category} npmInstallAttempted={NpmInstallAttempted} " +
                    "rateLimited={RateLimited} versionBefore={VersionBefore} versionAfter={VersionAfter} error={Error}",
                    cliType, record.Category, outcome.NpmInstallAttempted, outcome.RateLimited,
                    outcome.VersionBefore, outcome.VersionAfter, outcome.Error);
            }
            else if (outcome.NpmInstallAttempted || outcome.Actions.Count > 0)
            {
                logger.LogInformation(
                    "cli-self-heal-succeeded cli={CliType} at={At:o} category={Category} " +
                    "versionBefore={VersionBefore} versionAfter={VersionAfter} actions={Actions}",
                    cliType, utcNow, record.Category, outcome.VersionBefore, outcome.VersionAfter,
                    string.Join("; ", outcome.Actions));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CliSelfHealJournal: failed to append cli-self-heal.jsonl for {CliType}", cliType);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/cli-self-heal.jsonl</c>.</summary>
public sealed record CliSelfHealRecord
{
    public DateTime At { get; init; }
    public string CliType { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Healed { get; init; }
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public bool NpmInstallAttempted { get; init; }
    public bool RateLimited { get; init; }
    public string? Error { get; init; }
}
