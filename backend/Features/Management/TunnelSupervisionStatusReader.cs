using System.Text.Json;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public sealed class TunnelSupervisionStatusReader
{
    private static readonly TimeSpan WatchdogFreshness = TimeSpan.FromMinutes(3);
    private readonly IConfiguration _configuration;
    private readonly ILogger<TunnelSupervisionStatusReader> _logger;

    public TunnelSupervisionStatusReader(
        IConfiguration configuration,
        ILogger<TunnelSupervisionStatusReader> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Contract.TunnelSupervisionStatusDto? Read()
    {
        var stateDirectory = ResolveStateDirectory();
        if (string.IsNullOrWhiteSpace(stateDirectory)) return null;

        try
        {
            using var registration = ReadJson(Path.Combine(stateDirectory, "registration.json"));
            if (registration is null) return null;

            var sshTarget = String(registration.RootElement, "sshTarget");
            var remotePort = Int32(registration.RootElement, "remotePort");
            if (string.IsNullOrWhiteSpace(sshTarget) || remotePort is null) return null;

            using var watchdog = ReadJson(Path.Combine(stateDirectory, "watchdog.json"));
            using var keeper = ReadJson(Path.Combine(stateDirectory, "keeper.json"));
            var watchdogObservedAt = Date(watchdog?.RootElement, "observedAt");
            var watchdogFresh = watchdogObservedAt is { } observed
                && DateTime.UtcNow - observed <= WatchdogFreshness;
            var watchdogReportedState = String(watchdog?.RootElement, "status");
            var keeperReportedState = String(watchdog?.RootElement, "keeperTaskState");

            return new Contract.TunnelSupervisionStatusDto(
                sshTarget,
                remotePort.Value,
                new Contract.TunnelScheduledTaskStatusDto(
                    Boolean(registration.RootElement, "keeperRegistered"),
                    ResolveKeeperState(keeperReportedState, watchdogFresh),
                    Date(keeper?.RootElement, "observedAt")),
                new Contract.TunnelScheduledTaskStatusDto(
                    Boolean(registration.RootElement, "watchdogRegistered"),
                    ResolveWatchdogState(watchdogReportedState, watchdogFresh),
                    watchdogObservedAt),
                Date(watchdog?.RootElement, "lastHealAt"),
                String(watchdog?.RootElement, "lastHealResult"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "tunnel-supervision-status-read-failed stateDirectory={StateDirectory}", stateDirectory);
            return null;
        }
    }

    private string? ResolveStateDirectory()
    {
        var configured = _configuration["TunnelSupervision:StateDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        if (!OperatingSystem.IsWindows()) return null;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "Agent Studio", "Tunnel", "state");
    }

    private static JsonDocument? ReadJson(string path)
        => File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;

    private static string ResolveKeeperState(string? taskState, bool watchdogFresh)
    {
        if (!watchdogFresh) return "stale";
        return string.Equals(taskState, "Running", StringComparison.OrdinalIgnoreCase)
            ? "running"
            : string.IsNullOrWhiteSpace(taskState) ? "unknown" : taskState.ToLowerInvariant();
    }

    private static string ResolveWatchdogState(string? state, bool fresh)
    {
        if (!fresh) return "stale";
        return string.IsNullOrWhiteSpace(state) ? "unknown" : state.ToLowerInvariant();
    }

    private static string? String(JsonElement? element, string property)
        => element is { } value && value.TryGetProperty(property, out var item)
            && item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;

    private static int? Int32(JsonElement element, string property)
        => element.TryGetProperty(property, out var item) && item.TryGetInt32(out var value)
            ? value
            : null;

    private static bool Boolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var item)
            && item.ValueKind is JsonValueKind.True;

    private static DateTime? Date(JsonElement? element, string property)
        => DateTime.TryParse(String(element, property), out var value)
            ? value.ToUniversalTime()
            : null;
}

internal static class TunnelSupervisionProjection
{
    public static IReadOnlyList<Contract.RunnerCapabilitySnapshotDto> Attach(
        IReadOnlyList<Contract.RunnerCapabilitySnapshotDto> snapshots,
        Contract.TunnelSupervisionStatusDto? status)
    {
        if (status is null) return snapshots;
        var identity = $"127.0.0.1:{status.RemotePort}";
        return snapshots
            .Select(snapshot => snapshot.Capabilities.Any(capability =>
                string.Equals(capability.Key, Contract.CapabilityProtocol.TaskServerConnectivity, StringComparison.Ordinal)
                && string.Equals(capability.Identity, identity, StringComparison.OrdinalIgnoreCase))
                    ? snapshot with { TunnelSupervision = status }
                    : snapshot)
            .ToArray();
    }
}
