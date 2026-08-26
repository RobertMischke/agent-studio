using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// One account-level CLI limitation shared by every project runner in this
/// backend. The provider account is shared too, so a project-local breaker is
/// the wrong scope: it either lets the next project hit the same rejection or
/// pauses unrelated CLIs with the whole project.
/// </summary>
public sealed record ProviderLimitStatus(
    string CliType,
    string Status,
    DateTime DetectedAt,
    DateTime LimitedUntil,
    string Reason,
    bool ProbeInFlight,
    int ConsecutiveLimits,
    string? ProbeJobId = null);

public sealed record ProviderLimitObservation(
    string CliType,
    DateTime ObservedAt,
    DateTime RetryAt,
    string Reason,
    bool ResetTimeReported);

public enum ProviderLimitAdmission
{
    Ready,
    Limited,
    Probe,
}

/// <summary>Pure parser for account-level usage/session-limit reset hints.</summary>
public static partial class ProviderLimitParser
{
    public static readonly TimeSpan MissingResetRetry = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"\bresetsAt\s*=\s*(?<epoch>\d{10,13})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpochResetRegex();

    [GeneratedRegex(@"\breset(?:s)?\s+in\s+(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>hours?|hrs?|h|minutes?|mins?|m)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeResetRegex();

    [GeneratedRegex(@"\breset(?:s)?(?:\s+at)?\s+(?<time>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?))(?:\s*\((?<zone>[^)]+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex ClockResetRegex();

    public static ProviderLimitObservation Parse(
        string cliType,
        IEnumerable<CliOutputLine> output,
        DateTime observedAtUtc,
        TimeZoneInfo? localTimeZone = null)
    {
        var lines = output
            .Select(line => line.Text?.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Cast<string>()
            .ToArray();
        var text = string.Join("\n", lines);
        var observed = observedAtUtc.Kind == DateTimeKind.Utc
            ? observedAtUtc
            : observedAtUtc.ToUniversalTime();

        var epoch = EpochResetRegex().Match(text);
        if (epoch.Success && long.TryParse(epoch.Groups["epoch"].Value, out var rawEpoch))
        {
            var seconds = rawEpoch > 9_999_999_999 ? rawEpoch / 1000 : rawEpoch;
            if (seconds is >= 0 and <= 253_402_300_799)
            {
                var reset = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                if (reset > observed)
                    return Observation(cliType, observed, reset, lines, resetReported: true);
            }
        }

        var relative = RelativeResetRegex().Match(text);
        if (relative.Success
            && double.TryParse(
                relative.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
            && amount > 0)
        {
            var unit = relative.Groups["unit"].Value;
            var delay = unit.StartsWith("h", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromHours(amount)
                : TimeSpan.FromMinutes(amount);
            return Observation(cliType, observed, observed + delay, lines, resetReported: true);
        }

        var clock = ClockResetRegex().Match(text);
        if (clock.Success
            && DateTime.TryParseExact(
                NormalizeClock(clock.Groups["time"].Value),
                ["h:mmtt", "hh:mmtt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var time))
        {
            var zone = ResolveTimeZone(clock.Groups["zone"].Value) ?? localTimeZone ?? TimeZoneInfo.Local;
            try
            {
                var localObserved = TimeZoneInfo.ConvertTimeFromUtc(observed, zone);
                var localReset = localObserved.Date.Add(time.TimeOfDay);
                if (localReset <= localObserved) localReset = localReset.AddDays(1);
                var reset = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localReset, DateTimeKind.Unspecified), zone);
                return Observation(cliType, observed, reset, lines, resetReported: true);
            }
            catch (ArgumentException ex)
            {
                // A malformed/unsupported zone or a daylight-saving gap must
                // degrade to the bounded fallback, never fail run finalisation.
                AgentStudio.Diagnostics.SilentCatch.Note(
                    ex,
                    "ProviderLimitParser: invalid provider reset clock or time zone");
            }
        }

        return Observation(
            cliType,
            observed,
            observed + MissingResetRetry,
            lines,
            resetReported: false);
    }

    private static ProviderLimitObservation Observation(
        string cliType,
        DateTime observed,
        DateTime reset,
        IReadOnlyList<string> lines,
        bool resetReported)
    {
        var detail = lines.FirstOrDefault(line =>
                         line.Contains("session limit", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                     ?? "provider account limit reported by the CLI";
        if (detail.Length > 300) detail = detail[..300];
        return new ProviderLimitObservation(
            cliType.Trim().ToLowerInvariant(),
            observed,
            reset,
            detail,
            resetReported);
    }

    private static string NormalizeClock(string value)
        => value.Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    private static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}

/// <summary>
/// Durable fleet-level provider limit registry. At reset it admits exactly one
/// card as the provider probe; other cards for that CLI remain paused until the
/// probe produces a non-limit terminal response.
/// </summary>
public sealed class ProviderLimitStateStore
{
    private const string FileName = "provider-limits.json";
    private readonly object _gate = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderLimitStateStore> _logger;
    private readonly bool _persist;
    private readonly Dictionary<string, ProviderLimitStatus> _limits = new(StringComparer.OrdinalIgnoreCase);

    public ProviderLimitStateStore(
        IConfiguration configuration,
        ILogger<ProviderLimitStateStore> logger,
        bool persist = true)
    {
        _configuration = configuration;
        _logger = logger;
        _persist = persist;
        Load();
    }

    public ProviderLimitStatus Record(ProviderLimitObservation observation)
    {
        lock (_gate)
        {
            _limits.TryGetValue(observation.CliType, out var previous);
            var retryAt = observation.RetryAt > observation.ObservedAt
                ? observation.RetryAt
                : observation.ObservedAt + ProviderLimitParser.MissingResetRetry;
            var status = new ProviderLimitStatus(
                observation.CliType,
                "limited",
                previous?.DetectedAt ?? observation.ObservedAt,
                retryAt,
                observation.Reason,
                ProbeInFlight: false,
                ConsecutiveLimits: (previous?.ConsecutiveLimits ?? 0) + 1);
            _limits[observation.CliType] = status;
            PersistLocked();
            return status;
        }
    }

    public ProviderLimitAdmission Evaluate(string? cliType, DateTime nowUtc, bool mayProbe)
    {
        var admission = Peek(cliType, nowUtc);
        if (admission != ProviderLimitAdmission.Probe || !mayProbe)
            return admission == ProviderLimitAdmission.Probe
                ? ProviderLimitAdmission.Limited
                : admission;
        return TryBeginProbe(cliType, nowUtc)
            ? ProviderLimitAdmission.Probe
            : ProviderLimitAdmission.Limited;
    }

    /// <summary>Inspects admission without reserving the single recovery probe.</summary>
    public ProviderLimitAdmission Peek(string? cliType, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return ProviderLimitAdmission.Ready;
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var state))
                return ProviderLimitAdmission.Ready;
            var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
            if (now < state.LimitedUntil || state.ProbeInFlight)
                return ProviderLimitAdmission.Limited;
            return ProviderLimitAdmission.Probe;
        }
    }

    /// <summary>Atomically reserves the due recovery probe for one card.</summary>
    public bool TryBeginProbe(string? cliType, DateTime nowUtc, string? probeJobId = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var state)) return false;
            var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
            if (now < state.LimitedUntil || state.ProbeInFlight) return false;
            _limits[cliType] = state with
            {
                Status = "probing",
                ProbeInFlight = true,
                ProbeJobId = probeJobId,
            };
            PersistLocked();
            return true;
        }
    }

    /// <summary>
    /// Returns an unstarted probe reservation to limited state. This is used
    /// when local admission or process spawn fails after pickup selected the
    /// probe but before a provider response exists.
    /// </summary>
    public void ReleaseProbe(string? cliType, DateTime retryAtUtc, string? probeJobId = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return;
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var state)
                || !state.ProbeInFlight
                || !ProbeOwnerMatches(state, probeJobId)) return;
            var retryAt = retryAtUtc.Kind == DateTimeKind.Utc
                ? retryAtUtc
                : retryAtUtc.ToUniversalTime();
            _limits[cliType] = state with
            {
                Status = "limited",
                ProbeInFlight = false,
                ProbeJobId = null,
                LimitedUntil = retryAt,
            };
            PersistLocked();
        }
    }

    public void MarkHealthy(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return;
        lock (_gate)
        {
            if (!_limits.Remove(cliType)) return;
            PersistLocked();
        }
    }

    public bool TryGet(string? cliType, out ProviderLimitStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var found)) return false;
            status = found;
            return true;
        }
    }

    /// <summary>A non-limit response clears a pause only when this run owned the reset probe.</summary>
    public bool MarkProbeHealthy(string? cliType, string? probeJobId = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var state)
                || !state.ProbeInFlight
                || !ProbeOwnerMatches(state, probeJobId))
                return false;
            _limits.Remove(cliType);
            PersistLocked();
            return true;
        }
    }

    private static bool ProbeOwnerMatches(ProviderLimitStatus state, string? probeJobId)
        => string.IsNullOrWhiteSpace(state.ProbeJobId)
            ? string.IsNullOrWhiteSpace(probeJobId)
            : string.Equals(state.ProbeJobId, probeJobId, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderLimitStatus> Snapshot()
    {
        lock (_gate)
        {
            return _limits.Values
                .OrderBy(item => item.CliType, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private void Load()
    {
        var path = StorePath();
        if (path is null || !File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<ProviderLimitStatus[]>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var item in stored.Where(item => !string.IsNullOrWhiteSpace(item.CliType)))
                _limits[item.CliType] = item with
                {
                    ProbeInFlight = false,
                    ProbeJobId = null,
                    Status = "limited",
                };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read durable provider limit state from {Path}", path);
        }
    }

    private void PersistLocked()
    {
        var path = StorePath();
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                _limits.Values.OrderBy(item => item.CliType).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist provider limit state to {Path}", path);
        }
    }

    private string? StorePath()
    {
        if (!_persist) return null;
        var taskRepository = _configuration["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepository))
            return Path.Combine(taskRepository, ".runtime", FileName);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? null
            : Path.Combine(local, "agent-taskboard", FileName);
    }
}
