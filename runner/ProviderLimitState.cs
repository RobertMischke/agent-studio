using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>
/// Durable, provider-scoped account limit observed by a coding run. A provider
/// limit is host capability state, not a task outcome. Keeping it outside the
/// task runner lets Claude claims pause while Codex remains eligible.
/// </summary>
public sealed record ProviderLimitSnapshot(
    string CliType,
    DateTime ObservedAt,
    DateTime LimitedUntil,
    string Reason,
    bool AwaitingRecoveryProbe = true);

public sealed class ProviderLimitState
{
    public const string LimitedStatus = "limited";
    public static readonly TimeSpan UnknownResetDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RecoveryProbeInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, ProviderLimitSnapshot> _limits;
    private long _version;

    public ProviderLimitState(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "provider-limits.json");
        _limits = Load(_path);
    }

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public ProviderLimitSnapshot? Current(string? cliType)
    {
        var key = Normalize(cliType);
        lock (_gate) return _limits.GetValueOrDefault(key);
    }

    public ProviderLimitSnapshot Observe(
        string cliType,
        string? output,
        DateTime observedAtUtc)
    {
        var key = Normalize(cliType);
        var observedAt = DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc);
        var resetAt = ProviderLimitParser.ParseResetAt(output, observedAt)
                      ?? observedAt.Add(UnknownResetDelay);
        var reason = ProviderLimitParser.ExtractReason(output)
                     ?? $"{key} provider session or rate limit reached";
        var next = new ProviderLimitSnapshot(key, observedAt, resetAt, reason);

        lock (_gate)
        {
            if (_limits.TryGetValue(key, out var existing)
                && existing.LimitedUntil > next.LimitedUntil)
            {
                next = next with { LimitedUntil = existing.LimitedUntil };
            }
            _limits[key] = next;
            _version++;
            PersistLocked();
            return next;
        }
    }

    /// <summary>
    /// Clears a limit only after its advertised reset has passed and a bounded
    /// provider probe succeeds. A failed probe extends the pause by one minute,
    /// so a reset-time or timezone mismatch cannot restart a claim storm.
    /// </summary>
    public async Task<bool> ProbeRecoveryAsync(
        string cliType,
        DateTime nowUtc,
        Func<CancellationToken, Task<bool>> probe,
        CancellationToken ct)
    {
        var key = Normalize(cliType);
        ProviderLimitSnapshot? current;
        lock (_gate) current = _limits.GetValueOrDefault(key);
        if (current is null) return true;
        if (nowUtc.ToUniversalTime() < current.LimitedUntil) return false;

        var recovered = await probe(ct);
        lock (_gate)
        {
            current = _limits.GetValueOrDefault(key);
            if (current is null) return true;
            if (recovered)
            {
                _limits.Remove(key);
                _version++;
                PersistLocked();
                return true;
            }

            _limits[key] = current with
            {
                LimitedUntil = nowUtc.ToUniversalTime().Add(RecoveryProbeInterval),
                AwaitingRecoveryProbe = true,
            };
            _version++;
            PersistLocked();
            return false;
        }
    }

    public string AdvertisedDetail(string cliType)
    {
        var current = Current(cliType);
        return current is null
            ? string.Empty
            : $"{current.CliType}: limited until {current.LimitedUntil:O}. {current.Reason}";
    }

    private void PersistLocked()
    {
        var temp = _path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_limits.Values, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static Dictionary<string, ProviderLimitSnapshot> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = JsonSerializer.Deserialize<List<ProviderLimitSnapshot>>(
                             File.ReadAllText(path), Json)
                         ?? [];
            return values.ToDictionary(item => Normalize(item.CliType), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidDataException($"Provider limit state is unreadable: {path}", ex);
        }
    }

    private static string Normalize(string? cliType)
        => string.IsNullOrWhiteSpace(cliType) ? "unknown" : cliType.Trim().ToLowerInvariant();
}

public static partial class ProviderLimitParser
{
    [GeneratedRegex("\\bresetsAt\\s*[=:]\\s*[\\\"']?(?<epoch>\\d{10})", RegexOptions.IgnoreCase)]
    private static partial Regex EpochReset();

    [GeneratedRegex(@"\breset(?:s)?\s+in\s+(?<hours>\d+(?:[\.,]\d+)?)\s*h", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeHours();

    [GeneratedRegex(@"\breset(?:s)?\s+(?<clock>\d{1,2}:\d{2}\s*(?:am|pm)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ClockReset();

    public static bool IsProviderLimit(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("session limit", StringComparison.OrdinalIgnoreCase)
               || output.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
               || output.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase)
               || output.Contains("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase)
               || output.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
               || output.Contains("status=rejected", StringComparison.OrdinalIgnoreCase)
               || output.Contains("· rejected ·", StringComparison.OrdinalIgnoreCase);
    }

    public static DateTime? ParseResetAt(string? output, DateTime observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var epoch = EpochReset().Match(output);
        if (epoch.Success
            && long.TryParse(
                epoch.Groups["epoch"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds is >= -62135596800 and <= 253402300799)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        var relative = RelativeHours().Match(output);
        if (relative.Success
            && double.TryParse(
                relative.Groups["hours"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var hours)
            && hours > 0)
        {
            return observedAtUtc.ToUniversalTime().AddHours(hours);
        }

        var clock = ClockReset().Match(output);
        if (!clock.Success) return null;
        var formats = new[] { "h:mmtt", "hh:mmtt", "H:mm", "HH:mm" };
        var value = Regex.Replace(clock.Groups["clock"].Value, @"\s+", string.Empty);
        if (!DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return null;
        }

        var localObserved = observedAtUtc.ToUniversalTime().ToLocalTime();
        var localReset = localObserved.Date.Add(parsed.TimeOfDay);
        if (localReset <= localObserved) localReset = localReset.AddDays(1);
        return localReset.ToUniversalTime();
    }

    public static string? ExtractReason(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(IsProviderLimit);
    }
}
