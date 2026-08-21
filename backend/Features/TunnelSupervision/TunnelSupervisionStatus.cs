using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TunnelSupervision;

/// <summary>
/// Mirrors the keeper's own <c>%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\state.json</c>
/// (written by <c>deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1</c>).
/// </summary>
public sealed record TunnelKeeperStatus(
    string TaskName,
    bool Registered,
    string? State,
    string? LastStatus,
    [property: JsonConverter(typeof(LenientNullableDateTimeConverter))] DateTime? LastObservedAt,
    string? LastMessage);

/// <summary>
/// Mirrors the watchdog's status snapshot (written by
/// <c>deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh</c> next to its journal).
/// </summary>
public sealed record TunnelWatchdogStatus(
    string TaskName,
    bool Registered,
    string? State,
    [property: JsonConverter(typeof(LenientNullableDateTimeConverter))] DateTime? LastProbeAt,
    string? LastProbeResult,
    [property: JsonConverter(typeof(LenientNullableDateTimeConverter))] DateTime? LastHealAt,
    string? LastHealResult,
    int? ConsecutiveProbeFailures);

/// <summary>
/// The status file is written by an independently-versioned shell/PowerShell
/// pair, not by this backend. An unset optional timestamp has shown up as an
/// empty string rather than a JSON <c>null</c> (see AGT-2664 regression test
/// ShellWriterShapedFile_WithEmptyStringTimestamps_StillReportsHealthy) - treat
/// that the same as absent instead of failing the whole snapshot over one
/// optional field.
/// </summary>
internal sealed class LenientNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }
        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Combined snapshot written by
/// <c>deploy/windows/agent-runner-tunnel/setup-tunnel-supervision.ps1</c> to
/// <c>%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\supervision-status.json</c>.
/// </summary>
public sealed record TunnelSupervisionSnapshot(
    int SchemaVersion,
    DateTime GeneratedAt,
    TunnelKeeperStatus Keeper,
    TunnelWatchdogStatus Watchdog);

public static class TunnelSupervisionStatuses
{
    public const string NotConfigured = "not-configured";
    public const string Healthy = "healthy";
    public const string Attention = "attention";
    public const string Stale = "stale";
}

/// <summary>
/// Pure classification of one snapshot into the overall status an operator
/// sees on the Execution Hosts card. No I/O, no clock reads beyond the
/// supplied <paramref name="now"/> - see backend-structure styleguide
/// §"pure decision" and the matrix tests in TunnelSupervisionPolicyTests.
/// </summary>
public static class TunnelSupervisionPolicy
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public static string Classify(TunnelSupervisionSnapshot? snapshot, DateTime now)
    {
        if (snapshot is null) return TunnelSupervisionStatuses.NotConfigured;

        var freshest = snapshot.GeneratedAt;
        if (snapshot.Keeper.LastObservedAt is { } keeperAt && keeperAt > freshest) freshest = keeperAt;
        if (snapshot.Watchdog.LastProbeAt is { } probeAt && probeAt > freshest) freshest = probeAt;
        if (now - freshest > StaleAfter) return TunnelSupervisionStatuses.Stale;

        var attention =
            !snapshot.Keeper.Registered
            || !snapshot.Watchdog.Registered
            || !string.Equals(snapshot.Keeper.State, "Running", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.Watchdog.State, "Running", StringComparison.OrdinalIgnoreCase)
            || snapshot.Keeper.LastStatus == "unreachable"
            || snapshot.Watchdog.LastProbeResult == "failed"
            || snapshot.Watchdog.LastHealResult == "failed";

        return attention ? TunnelSupervisionStatuses.Attention : TunnelSupervisionStatuses.Healthy;
    }
}

/// <summary>
/// Reads the combined status file written by <c>setup-tunnel-supervision.ps1</c>.
/// The file only ever exists on a Windows control-plane host that has run the
/// guided registration; everywhere else this is a fast, quiet no-op. The
/// backend never writes this file or triggers registration - reading is the
/// entire side effect (see docs/operations/setup/windows-control-plane-host.md).
/// </summary>
public sealed class TunnelSupervisionStatusReader(IConfiguration config, ILogger<TunnelSupervisionStatusReader> logger)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private string StatusPath => config["TunnelSupervision:StatusFilePath"]
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentTaskboard", "tunnel-keeper", "supervision-status.json");

    public TunnelSupervisionSnapshot? Read()
    {
        var path = StatusPath;
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TunnelSupervisionSnapshot>(File.ReadAllText(path), _json); }
        catch (Exception ex)
        {
            // A present-but-unparseable file is a real signal (a version skew
            // between the writer script and this reader, or a half-written
            // file) that "not-configured" alone would hide - log it so an
            // operator chasing a missing panel finds the cause here instead
            // of nothing.
            logger.LogWarning(ex, "tunnel-supervision-status-read-failed path={Path}", path);
            return null;
        }
    }
}
