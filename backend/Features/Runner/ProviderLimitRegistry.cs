using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Account-level CLI limit observed from a provider rejection. This is a
/// capability state, not a task outcome: every card using the same CLI shares
/// it, while cards routed to another CLI remain eligible.
/// </summary>
public sealed record ProviderLimitStatus(
    string CliType,
    DateTime ObservedAt,
    DateTime LimitedUntil,
    string Reason,
    bool ResetTimeReported);

/// <summary>Pure parser for provider session/rate-limit rejections.</summary>
public static partial class ProviderLimitDetector
{
    public static readonly TimeSpan UnknownResetRetry = TimeSpan.FromMinutes(15);

    private static readonly string[] ExhaustedNeedles =
    [
        "hit your session limit",
        "session limit",
        "session limit reached",
        "usage limit",
        "usage limit reached",
        "you've reached your usage limit",
        "quota exceeded",
        "rate limit exceeded",
        "rate_limit_exceeded",
        "insufficient_quota",
        "too many requests",
        "429",
        "status=rejected",
        "· rejected ·",
    ];

    public static ProviderLimitStatus? Detect(
        string? cliType,
        IEnumerable<string?> output,
        DateTime utcNow,
        TimeZoneInfo? localZone = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        var text = string.Join('\n', output.Where(line => !string.IsNullOrWhiteSpace(line)));
        if (!ExhaustedNeedles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            return null;

        var observedAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var resetAt = ParseResetAt(text, observedAt, localZone ?? TimeZoneInfo.Local);
        var reported = resetAt is not null;
        var limitedUntil = resetAt ?? observedAt.Add(UnknownResetRetry);
        var detail = FirstLimitLine(text);
        var reason = reported
            ? $"{cliType.Trim().ToLowerInvariant()}: limited until {limitedUntil:O} ({detail})"
            : $"{cliType.Trim().ToLowerInvariant()}: provider limit detected; retry probe at {limitedUntil:O} ({detail})";
        return new ProviderLimitStatus(
            cliType.Trim().ToLowerInvariant(),
            observedAt,
            limitedUntil,
            reason,
            reported);
    }

    private static DateTime? ParseResetAt(string text, DateTime utcNow, TimeZoneInfo localZone)
    {
        var epoch = EpochResetRegex().Match(text);
        if (epoch.Success
            && long.TryParse(epoch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        var iso = IsoResetRegex().Match(text);
        if (iso.Success
            && DateTimeOffset.TryParse(
                iso.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var absolute))
            return absolute.UtcDateTime;

        var clock = ClockResetRegex().Match(text);
        if (!clock.Success) return null;
        if (!DateTime.TryParse(
                clock.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return null;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, localZone);
        var localReset = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            parsed.Hour,
            parsed.Minute,
            0,
            DateTimeKind.Unspecified);
        if (localReset <= localNow) localReset = localReset.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(localReset, localZone);
    }

    private static string FirstLimitLine(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExhaustedNeedles.Any(needle => line.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return line.Trim().Length <= 300 ? line.Trim() : line.Trim()[..300];
        }
        return "provider rejected the request at the account limit";
    }

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}(?::\d{2})?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetRegex();

    [GeneratedRegex(@"\bresetsAt\s*=\s*(?<value>\d{10})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochResetRegex();

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockResetRegex();
}

/// <summary>Process-wide provider circuit shared by every project runner.</summary>
public sealed class ProviderLimitRegistry
{
    public static readonly TimeSpan FailedProbeRetry = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ProviderLimitStatus> _limits =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _probes =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderLimitStatus Record(ProviderLimitStatus status)
    {
        return _limits.AddOrUpdate(
            status.CliType,
            status,
            (_, existing) => status.LimitedUntil >= existing.LimitedUntil ? status : existing);
    }

    public ProviderLimitStatus? GetActive(string? cliType, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        return _limits.TryGetValue(cliType, out var status) ? status : null;
    }

    public IReadOnlyList<ProviderLimitStatus> Active(DateTime utcNow)
    {
        return _limits.Values
            .OrderBy(status => status.CliType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Opens exactly one reset-time quota probe per CLI. The provider remains
    /// limited while the probe runs, so another project tick cannot admit a
    /// card against the same account before recovery is confirmed.
    /// </summary>
    public bool TryBeginRecoveryProbe(string? cliType, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(cliType)
            || !_limits.TryGetValue(cliType, out var status)
            || status.LimitedUntil > utcNow)
            return false;
        return _probes.TryAdd(cliType, 0);
    }

    public void CompleteRecoveryProbe(
        string cliType,
        DateTime utcNow,
        bool recovered,
        string? failureReason = null)
    {
        try
        {
            if (recovered)
            {
                _limits.TryRemove(cliType, out _);
                return;
            }

            if (_limits.TryGetValue(cliType, out var current))
            {
                var retryAt = utcNow.Add(FailedProbeRetry);
                _limits[cliType] = current with
                {
                    LimitedUntil = retryAt,
                    Reason = $"{cliType}: recovery probe still limited; retry at {retryAt:O}"
                             + (string.IsNullOrWhiteSpace(failureReason) ? string.Empty : $" ({failureReason})"),
                    ResetTimeReported = false,
                };
            }
        }
        finally
        {
            _probes.TryRemove(cliType, out _);
        }
    }
}
